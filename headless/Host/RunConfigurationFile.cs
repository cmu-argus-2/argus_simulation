using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.Simulation.Core;

namespace Argus.Simulation.Host
{
    // Reads a run configuration file (G5) and the spacecraft profile it names (D11,
    // docs/device-model.md §4) into a Core SimulationConfiguration. Both files mirror Core property
    // names in camelCase, in SI units with the unit in each name; headless/README.md documents the
    // format. Every field is required, except two gateway settings of the run file (CORE-9):
    // physicalActuators, whose absence is the safe value (inhibited), and heartbeatTimeoutWallSeconds,
    // whose absence turns the failsafe off. That is safe only while no controller attaches: the run
    // file can't lift the inhibit without a timeout, and HOST-6 is to refuse a controller session on a
    // run without one. Unknown fields and explicit nulls are rejected, so a typo never falls back to a
    // default. The run ID is not in the file: each run gets its own.
    public static class RunConfigurationFile
    {
        private const string EpochFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";
        private static readonly Regex ProfileIdPattern = new Regex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

        // The run file and its profile, resolved.
        public sealed class LoadedRun
        {
            internal LoadedRun(SimulationConfiguration configuration, string profileId, string profilePath, string profileFile)
            {
                Configuration = configuration;
                ProfileId = profileId;
                ProfilePath = profilePath;
                ProfileFile = profileFile;
            }

            public SimulationConfiguration Configuration { get; }

            // The profile's profileId.
            public string ProfileId { get; }

            // spacecraftProfile as the run file wrote it, or null for a profile given inline.
            public string ProfilePath { get; }

            // The profile file's full path, which error messages name, or null for a profile given
            // inline.
            public string ProfileFile { get; }
        }

        public static SimulationConfiguration Load(string path, string runId) => Read(path, runId).Configuration;

        public static LoadedRun Read(string path, string runId)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                throw new RunConfigurationException($"cannot read the file: {exception.Message}");
            }

            return Parse(text, runId, Path.GetDirectoryName(path));
        }

        // A run whose spacecraftProfile is the profile itself; a path needs Read, which knows where
        // the run file is.
        public static SimulationConfiguration Parse(string json, string runId) => Parse(json, runId, null).Configuration;

        private static LoadedRun Parse(string json, string runId, string runDirectory)
        {
            using (JsonDocument document = Document(json, ""))
            {
                JsonElement root = document.RootElement;
                // A run file from before the split is told where its body and devices went.
                foreach (string moved in new[] { "spacecraft", "devices" })
                {
                    Check(
                        root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(moved, out _),
                        moved,
                        "is no longer a run-file field; the body and devices are in the spacecraftProfile, the attitude and rate in initialAttitude (device-model §4)");
                }

                Fields(
                    root,
                    "",
                    new[] { "epochUtc", "fixedStepSeconds", "randomSeed", "kernelSetId", "initialOrbit", "initialAttitude", "spacecraftProfile" },
                    Array.Empty<string>(),
                    new[] { "heartbeatTimeoutWallSeconds", "physicalActuators" });

                DateTimeOffset epochUtc = Epoch(root, "epochUtc");
                double fixedStepSeconds = Number(root, "fixedStepSeconds", "");
                Check(new SimulationConfiguration(runId, epochUtc, fixedStepSeconds).IsValid, "fixedStepSeconds", "must be finite and > 0");
                int randomSeed = Integer(root, "randomSeed", "");
                Check(new SimulationConfiguration(runId, epochUtc, fixedStepSeconds, randomSeed: randomSeed).IsValid, "randomSeed", "must be >= 0");
                string kernelSetId = Text(root, "kernelSetId", "");
                Check(
                    new SimulationConfiguration(runId, epochUtc, fixedStepSeconds, kernelSetId: kernelSetId).IsValid,
                    "kernelSetId",
                    "must match [a-z0-9][a-z0-9._-]{0,63}");

                ClassicalOrbitElements orbit = Orbit(Field(root, "initialOrbit", ""), "initialOrbit");
                JsonElement attitude = Field(root, "initialAttitude", "");
                Fields(attitude, "initialAttitude", "bodyToJ2000", "angularVelocityBodyRadiansPerSecond");
                Quaterniond bodyToJ2000 = Quaternion(attitude, "bodyToJ2000", "initialAttitude");
                Vector3d angularVelocity = Vector(attitude, "angularVelocityBodyRadiansPerSecond", "initialAttitude");

                // The profile is a path relative to the run file (a team keeps it anywhere), or the
                // profile itself, which keeps a frozen reference case in one file.
                JsonElement profileField = Field(root, "spacecraftProfile", "");
                string profilePath = null;
                string profileFile = null;
                SpacecraftProfile profile;
                if (profileField.ValueKind == JsonValueKind.String)
                {
                    profilePath = profileField.GetString();
                    Check(profilePath.Trim().Length > 0, "spacecraftProfile", "must not be blank");
                    Check(runDirectory != null, "spacecraftProfile", "is a path, which only a run file read from disk can resolve");
                    profileFile = Path.GetFullPath(Path.Combine(runDirectory, profilePath));
                    profile = ReadProfileFile(profileFile, runId, epochUtc, fixedStepSeconds);
                }
                else
                {
                    Check(profileField.ValueKind == JsonValueKind.Object, "spacecraftProfile", "must be a path or a profile object");
                    profile = ReadProfile(profileField, "spacecraftProfile", runId, epochUtc, fixedStepSeconds);
                }

                SpacecraftConfiguration spacecraft = new SpacecraftConfiguration(
                    profile.MassKilograms, profile.Inertia, bodyToJ2000, angularVelocity);
                Check(
                    spacecraft.IsValid,
                    "initialAttitude",
                    "needs a unit quaternion and a finite rate");

                double heartbeatTimeoutWallSeconds = 0.0;
                if (root.TryGetProperty("heartbeatTimeoutWallSeconds", out JsonElement timeout))
                {
                    Check(timeout.ValueKind != JsonValueKind.Null, "heartbeatTimeoutWallSeconds", "must be a number; omit it to turn the failsafe off");
                    heartbeatTimeoutWallSeconds = Number(root, "heartbeatTimeoutWallSeconds", "");
                    Check(heartbeatTimeoutWallSeconds >= 0.0, "heartbeatTimeoutWallSeconds", "must be finite and >= 0");
                }

                PhysicalActuatorSetting physicalActuators = PhysicalActuatorSetting.Inhibited;
                if (root.TryGetProperty("physicalActuators", out JsonElement inhibit))
                {
                    Check(inhibit.ValueKind != JsonValueKind.Null, "physicalActuators", "must be an object; omit it to keep the inhibit on");
                    physicalActuators = PhysicalActuators(inhibit, "physicalActuators", heartbeatTimeoutWallSeconds);
                }

                SimulationConfiguration configuration = new SimulationConfiguration(
                    runId,
                    epochUtc,
                    fixedStepSeconds,
                    initialOrbit: orbit,
                    randomSeed: randomSeed,
                    kernelSetId: kernelSetId,
                    spacecraft: spacecraft,
                    devices: profile.Devices,
                    heartbeatTimeoutWallSeconds: heartbeatTimeoutWallSeconds,
                    physicalActuators: physicalActuators);
                Check(configuration.IsValid, "", "is not a valid run configuration");
                return new LoadedRun(configuration, profile.ProfileId, profilePath, profileFile);
            }
        }

        private static JsonDocument Document(string json, string path)
        {
            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                throw Error(path, $"not valid JSON: {exception.Message}");
            }
        }

        // A profile file; its errors name the file by its full path.
        private static SpacecraftProfile ReadProfileFile(string path, string runId, DateTimeOffset epochUtc, double fixedStepSeconds)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                throw Error("spacecraftProfile", $"cannot read the spacecraft profile {path}: {exception.Message}");
            }

            try
            {
                using (JsonDocument document = Document(text, ""))
                {
                    return ReadProfile(document.RootElement, "", runId, epochUtc, fixedStepSeconds);
                }
            }
            catch (RunConfigurationException exception)
            {
                throw new RunConfigurationException($"spacecraft profile {path}: {exception.Message}");
            }
        }

        // The body and its devices (docs/device-model.md §4). The initial attitude and rate vary per
        // run, so they are the run file's.
        private static SpacecraftProfile ReadProfile(JsonElement element, string path, string runId, DateTimeOffset epochUtc, double fixedStepSeconds)
        {
            Fields(element, path, "profileId", "body", "devices");
            string profileId = Text(element, "profileId", path);
            Check(ProfileIdPattern.IsMatch(profileId), Join(path, "profileId"), "must match [a-z0-9][a-z0-9._-]{0,63}");

            JsonElement body = Field(element, "body", path);
            string bodyPath = Join(path, "body");
            Fields(body, bodyPath, "massKilograms", "inertiaBodyKilogramSquareMeters");
            double massKilograms = Number(body, "massKilograms", bodyPath);
            Matrix3d inertia = Matrix(body, "inertiaBodyKilogramSquareMeters", bodyPath);
            // The body's checks need an attitude; the identity at rest leaves only the body's own.
            Check(
                new SpacecraftConfiguration(massKilograms, inertia, new Quaterniond(0.0, 0.0, 0.0, 1.0), new Vector3d(0.0, 0.0, 0.0)).IsValid,
                bodyPath,
                "needs mass > 0 and a symmetric positive-definite inertia");

            DeviceConfiguration[] devices = Devices(Field(element, "devices", path), Join(path, "devices"), runId, epochUtc, fixedStepSeconds);
            return new SpacecraftProfile(profileId, massKilograms, inertia, devices);
        }

        private sealed class SpacecraftProfile
        {
            public SpacecraftProfile(string profileId, double massKilograms, Matrix3d inertia, DeviceConfiguration[] devices)
            {
                ProfileId = profileId;
                MassKilograms = massKilograms;
                Inertia = inertia;
                Devices = devices;
            }

            public string ProfileId { get; }
            public double MassKilograms { get; }
            public Matrix3d Inertia { get; }
            public DeviceConfiguration[] Devices { get; }
        }

        // Present only to lift the inhibit: enabled, a test procedure, and a heartbeat failsafe
        // (CORE-9, SimulationConfiguration).
        private static PhysicalActuatorSetting PhysicalActuators(JsonElement element, string path, double heartbeatTimeoutWallSeconds)
        {
            Fields(element, path, "enabled", "testProcedure");
            bool enabled = Boolean(element, "enabled", path);
            string testProcedure = Text(element, "testProcedure", path);
            Check(
                enabled,
                Join(path, "enabled"),
                "is false, so the inhibit stays on; remove physicalActuators instead of naming a procedure");
            Check(!string.IsNullOrWhiteSpace(testProcedure), Join(path, "testProcedure"), "must not be blank");
            double maximum = SimulationConfiguration.MaximumHeartbeatTimeoutWithPhysicalActuatorsWallSeconds;
            Check(
                heartbeatTimeoutWallSeconds > 0.0 && heartbeatTimeoutWallSeconds <= maximum,
                Join(path, "enabled"),
                $"needs heartbeatTimeoutWallSeconds in (0, {maximum.ToString(CultureInfo.InvariantCulture)}] s, so a silent controller fails safe");
            return new PhysicalActuatorSetting(true, testProcedure);
        }

        private static ClassicalOrbitElements Orbit(JsonElement element, string path)
        {
            Fields(
                element,
                path,
                "semiMajorAxisMeters",
                "eccentricity",
                "inclinationDegrees",
                "raanDegrees",
                "argumentOfPeriapsisDegrees",
                "trueAnomalyDegrees");
            ClassicalOrbitElements orbit = new ClassicalOrbitElements(
                Number(element, "semiMajorAxisMeters", path),
                Number(element, "eccentricity", path),
                Number(element, "inclinationDegrees", path),
                Number(element, "raanDegrees", path),
                Number(element, "argumentOfPeriapsisDegrees", path),
                Number(element, "trueAnomalyDegrees", path));
            Check(orbit.IsValid, path, "needs finite elements, 0 <= eccentricity < 1, 0 <= inclination <= 180 and periapsis above the Earth's equatorial radius");
            return orbit;
        }

        private static DeviceConfiguration[] Devices(
            JsonElement element,
            string path,
            string runId,
            DateTimeOffset epochUtc,
            double fixedStepSeconds)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw Error(path, "must be an array");
            }

            DeviceConfiguration[] devices = new DeviceConfiguration[element.GetArrayLength()];
            Dictionary<string, int> indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, DeviceConfiguration> cameraByProfileId = new Dictionary<string, DeviceConfiguration>(StringComparer.Ordinal);
            int index = 0;
            foreach (JsonElement device in element.EnumerateArray())
            {
                string devicePath = $"{path}[{index}]";
                devices[index] = Device(device, devicePath);
                // Device() has checked everything else IsValid checks for one device, so only the
                // period can fail here.
                Check(
                    new SimulationConfiguration(runId, epochUtc, fixedStepSeconds, devices: new[] { devices[index] }).IsValid,
                    Join(devicePath, "samplePeriodSeconds"),
                    "must be a whole multiple of fixedStepSeconds");
                string deviceId = devices[index].Definition.SensorId;
                if (indexById.TryGetValue(deviceId, out int firstIndex))
                {
                    throw Error(Join(devicePath, "deviceId"), $"'{deviceId}' is already {path}[{firstIndex}]'s ID");
                }

                indexById.Add(deviceId, index);
                if (devices[index].KindDefinition is ICameraKind camera)
                {
                    // A profile ID names one parameter set (CameraProfile.IsSameParameterSetAs, CORE-6).
                    CameraProfile profile = camera.Profile(devices[index]);
                    if (cameraByProfileId.TryGetValue(profile.ProfileId, out DeviceConfiguration first))
                    {
                        CameraProfile firstProfile = ((ICameraKind)first.KindDefinition).Profile(first);
                        Check(
                            profile.IsSameParameterSetAs(firstProfile),
                            $"{devicePath}.parameters[\"profile_id\"]",
                            $"names another parameter set than {first.Definition.SensorId}: it differs in {DifferingParts(profile, firstProfile)}");
                    }
                    else
                    {
                        cameraByProfileId.Add(profile.ProfileId, devices[index]);
                    }
                }

                index++;
            }

            return devices;
        }

        // Which parts of a camera's parameter set differ, in the file's groups, for the profile_id error.
        // CameraProfile.IsSameParameterSetAs is the rule; keep the two in step.
        private static string DifferingParts(CameraProfile profile, CameraProfile first)
        {
            List<string> parts = new List<string>();
            if (!profile.Intrinsics.Equals(first.Intrinsics) ||
                !string.Equals(profile.IntrinsicsSource, first.IntrinsicsSource, StringComparison.Ordinal) ||
                profile.IsCalibrated != first.IsCalibrated)
            {
                parts.Add("intrinsics");
            }

            if (!profile.Distortion.Equals(first.Distortion))
            {
                parts.Add("distortion");
            }

            if (!string.Equals(profile.NoiseSource, first.NoiseSource, StringComparison.Ordinal))
            {
                parts.Add("noise");
            }

            // Falls back to the whole set if a versioned field is missing above.
            return parts.Count == 0 ? "its parameter set" : string.Join(" and ", parts);
        }

        // One device: the common fields, its registered kind and that kind's parameters
        // (docs/device-model.md §1). The kind declares each parameter's type, so values are read as
        // that type and never guessed from their JSON shape. Only sensors have samplePeriodSeconds
        // and controllerVisible: an actuator takes a command every step and publishes no frames.
        private static DeviceConfiguration Device(JsonElement element, string path)
        {
            Check(element.ValueKind == JsonValueKind.Object, path, "must be an object");
            // Which side models a device belongs to its kind and is not configured per device
            // (docs/device-model.md §1), so the file has no source: the host sets it from the kind
            // (CORE-22). An older file that still has one is told why.
            Check(
                !element.TryGetProperty("source", out _),
                Join(path, "source"),
                "is no longer a run-file field; the device's kind sets it (device-model §1)");
            string kindName = Text(element, "kind", path);
            IDeviceKind kind = DeviceKinds.Find(kindName);
            if (kind == null)
            {
                // Named by its ID too, so a team finds it in a long list.
                string named = element.TryGetProperty("deviceId", out JsonElement id) && id.ValueKind == JsonValueKind.String
                    ? $" of {id.GetString()}"
                    : "";
                throw Error(Join(path, "kind"), $"'{kindName}'{named} is not a registered device kind ({DeviceKinds.Names})");
            }

            bool isActuator = kind.Role == DeviceRole.Actuator;
            Fields(
                element,
                path,
                isActuator
                    ? new[] { "deviceId", "kind", "modelName", "frameId", "mount", "parameters" }
                    : new[] { "deviceId", "kind", "modelName", "frameId", "samplePeriodSeconds", "controllerVisible", "mount", "parameters" },
                Array.Empty<string>(),
                new[] { "initialState" });

            JsonElement mount = Field(element, "mount", path);
            string mountPath = Join(path, "mount");
            Fields(mount, mountPath, "positionBodyMeters", "deviceToBody");
            SensorMount sensorMount = new SensorMount(
                Vector(mount, "positionBodyMeters", mountPath),
                Quaternion(mount, "deviceToBody", mountPath));
            Check(sensorMount.IsValid, mountPath, "needs a finite position and a unit quaternion");
            Check(
                !isActuator || ActuatorGeometry.IsIdentityRotation(sensorMount.SensorToBody),
                Join(mountPath, "deviceToBody"),
                "must be the identity for an actuator, whose axis is a body-frame parameter");

            SensorDefinition definition = new SensorDefinition(
                Text(element, "deviceId", path),
                Text(element, "modelName", path),
                Text(element, "frameId", path),
                isActuator ? 0.0 : Number(element, "samplePeriodSeconds", path),
                DeviceSource.For(kind),
                sensorMount,
                !isActuator && Boolean(element, "controllerVisible", path));
            Check(
                isActuator ? definition.HasValidCommonFields : definition.IsValid,
                path,
                isActuator
                    ? "needs non-blank deviceId, modelName and frameId"
                    : "needs non-blank deviceId, modelName and frameId, and samplePeriodSeconds > 0");

            DeviceValues parameters = ReadValues(
                Field(element, "parameters", path), Join(path, "parameters"), kind.ParameterKeys, $"{kind.Name} parameter");
            // Optional: a kind with no state (every kind so far) needs none.
            DeviceValues initialState = element.TryGetProperty("initialState", out JsonElement state)
                ? ReadValues(state, Join(path, "initialState"), kind.InitialStateKeys, $"{kind.Name} initial-state key")
                : DeviceValues.Empty;

            DeviceConfiguration configuration = new DeviceConfiguration(definition, kindName, parameters, initialState);
            // The kind's problem starts with the field it names: parameters["noise_std_tesla"], ...
            string problem = configuration.FindProblem();
            if (problem != null)
            {
                throw new RunConfigurationException($"{path}.{problem}");
            }

            Check(
                !definition.IsControllerVisible || kind.ControllerProjection != null,
                Join(path, "controllerVisible"),
                $"must be false, because {kindName} frames carry truth a controller may not see (D7)");
            return configuration;
        }

        // Reads an object of declared keys, typed as the kind declares them. A key's group
        // (gyroscope.*) may be written as a nested object, which flattens with ".", but each key is
        // given once: as a dotted key or inside its group, not both.
        private static DeviceValues ReadValues(JsonElement element, string path, IReadOnlyList<DeviceKey> keys, string keyKind)
        {
            Dictionary<string, string> givenAt = new Dictionary<string, string>(StringComparer.Ordinal);
            DeviceValues.Builder values = new DeviceValues.Builder();
            ReadValues(element, path, "", keys, keyKind, values, givenAt);
            return values.Build();
        }

        private static void ReadValues(
            JsonElement element,
            string path,
            string prefix,
            IReadOnlyList<DeviceKey> keys,
            string keyKind,
            DeviceValues.Builder values,
            Dictionary<string, string> givenAt)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw Error(path, "must be an object");
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                string key = prefix + property.Name;
                // A dotted key is quoted, so it reads apart from the same key inside its group.
                string valuePath = property.Name.Contains(".")
                    ? $"{path}[\"{property.Name}\"]"
                    : Join(path, property.Name);
                DeviceValueType? type = DeclaredType(keys, key);
                if (type.HasValue)
                {
                    if (givenAt.TryGetValue(key, out string firstPath))
                    {
                        throw Error(valuePath, $"repeats {firstPath}");
                    }

                    givenAt.Add(key, valuePath);
                    values.Add(key, Value(property.Value, valuePath, type.Value));
                }
                else if (IsGroup(keys, key))
                {
                    ReadValues(property.Value, valuePath, key + ".", keys, keyKind, values, givenAt);
                }
                else
                {
                    throw Error(valuePath, $"is not a {keyKind}");
                }
            }
        }

        private static DeviceValueType? DeclaredType(IReadOnlyList<DeviceKey> keys, string key)
        {
            foreach (DeviceKey declared in keys)
            {
                if (declared.Name == key)
                {
                    return declared.Type;
                }
            }

            return null;
        }

        private static bool IsGroup(IReadOnlyList<DeviceKey> keys, string key)
        {
            foreach (DeviceKey declared in keys)
            {
                if (declared.Name.StartsWith(key + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static DeviceValue Value(JsonElement element, string path, DeviceValueType type)
        {
            switch (type)
            {
                case DeviceValueType.Number:
                    return DeviceValue.FromNumber(Number(element, path));
                case DeviceValueType.Vector3:
                    return DeviceValue.FromVector3(Vector(element, path));
                case DeviceValueType.Quaternion:
                    return DeviceValue.FromQuaternion(Quaternion(element, path));
                case DeviceValueType.Matrix3:
                    return DeviceValue.FromMatrix3(Matrix(element, path));
                case DeviceValueType.Flag:
                    if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
                    {
                        throw Error(path, "must be true or false");
                    }

                    return DeviceValue.FromFlag(element.GetBoolean());
                case DeviceValueType.Text:
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        throw Error(path, "must be a string");
                    }

                    return DeviceValue.FromText(element.GetString());
                default:
                    throw Error(path, $"has a declared type {type} the run file cannot read");
            }
        }

        // An object with exactly these fields.
        private static void Fields(JsonElement element, string path, params string[] required) =>
            Fields(element, path, required, Array.Empty<string>());

        // An object with every required field, exactly one of the oneOf fields when any are listed,
        // any of the optional fields, and nothing else.
        private static void Fields(JsonElement element, string path, string[] required, string[] oneOf) =>
            Fields(element, path, required, oneOf, Array.Empty<string>());

        private static void Fields(JsonElement element, string path, string[] required, string[] oneOf, string[] optional)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw Error(path, "must be an object");
            }

            foreach (string name in required)
            {
                Field(element, name, path);
            }

            int oneOfCount = 0;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (Array.IndexOf(oneOf, property.Name) >= 0)
                {
                    oneOfCount++;
                }
                else if (Array.IndexOf(required, property.Name) < 0 && Array.IndexOf(optional, property.Name) < 0)
                {
                    throw Error(Join(path, property.Name), "is not a known field");
                }
            }

            if (oneOf.Length > 0 && oneOfCount != 1)
            {
                throw Error(path, $"needs exactly one of {string.Join(", ", oneOf)}");
            }
        }

        private static JsonElement Field(JsonElement element, string name, string path)
        {
            if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                throw Error(Join(path, name), "is required");
            }

            return value;
        }

        private static double Number(JsonElement element, string name, string path) =>
            Number(Field(element, name, path), Join(path, name));

        private static double Number(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double value) ||
                double.IsNaN(value) || double.IsInfinity(value))
            {
                throw Error(path, "must be a finite number");
            }

            return value;
        }

        private static int Integer(JsonElement element, string name, string path)
        {
            JsonElement value = Field(element, name, path);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            {
                throw Error(Join(path, name), "must be a 32-bit integer");
            }

            return result;
        }

        private static bool Boolean(JsonElement element, string name, string path)
        {
            JsonElement value = Field(element, name, path);
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
            {
                throw Error(Join(path, name), "must be true or false");
            }

            return value.GetBoolean();
        }

        private static string Text(JsonElement element, string name, string path)
        {
            JsonElement value = Field(element, name, path);
            if (value.ValueKind != JsonValueKind.String)
            {
                throw Error(Join(path, name), "must be a string");
            }

            return value.GetString();
        }

        private static DateTimeOffset Epoch(JsonElement element, string name)
        {
            string text = Text(element, name, "");
            if (!DateTimeOffset.TryParseExact(
                    text,
                    EpochFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset epoch))
            {
                throw Error(name, $"must be a UTC time such as 2025-01-15T00:00:00Z, got '{text}'");
            }

            return epoch;
        }

        // [x, y, z].
        private static Vector3d Vector(JsonElement element, string name, string path) =>
            Vector(Field(element, name, path), Join(path, name));

        private static Vector3d Vector(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
            {
                throw Error(path, "must be an array of 3 numbers");
            }

            return new Vector3d(
                Number(element[0], $"{path}[0]"),
                Number(element[1], $"{path}[1]"),
                Number(element[2], $"{path}[2]"));
        }

        // { "x", "y", "z", "w" }, named so the scalar's position is never ambiguous.
        private static Quaterniond Quaternion(JsonElement element, string name, string path) =>
            Quaternion(Field(element, name, path), Join(path, name));

        private static Quaterniond Quaternion(JsonElement value, string quaternionPath)
        {
            Fields(value, quaternionPath, "x", "y", "z", "w");
            return new Quaterniond(
                Number(value, "x", quaternionPath),
                Number(value, "y", quaternionPath),
                Number(value, "z", quaternionPath),
                Number(value, "w", quaternionPath));
        }

        // Three rows, each [x, y, z].
        private static Matrix3d Matrix(JsonElement element, string name, string path) =>
            Matrix(Field(element, name, path), Join(path, name));

        private static Matrix3d Matrix(JsonElement value, string matrixPath)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            {
                throw Error(matrixPath, "must be an array of 3 rows");
            }

            return new Matrix3d(
                Vector(value[0], $"{matrixPath}[0]"),
                Vector(value[1], $"{matrixPath}[1]"),
                Vector(value[2], $"{matrixPath}[2]"));
        }

        private static void Check(bool condition, string path, string reason)
        {
            if (!condition)
            {
                throw Error(path, reason);
            }
        }

        private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

        private static RunConfigurationException Error(string path, string reason) =>
            new RunConfigurationException(path.Length == 0 ? reason : $"{path}: {reason}");
    }
}
