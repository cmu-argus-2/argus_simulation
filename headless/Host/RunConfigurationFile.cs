using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Argus.Simulation.Core;

namespace Argus.Simulation.Host
{
    // Reads a run configuration file (G5) into a Core SimulationConfiguration. The file mirrors Core
    // property names in camelCase, in SI units with the unit in each name; headless/README.md
    // documents the format. Every field is required and unknown fields are rejected, so a typo never
    // falls back to a default. The run ID is not in the file: each run gets its own.
    public static class RunConfigurationFile
    {
        private const string EpochFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

        public static SimulationConfiguration Load(string path, string runId)
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

            return Parse(text, runId);
        }

        public static SimulationConfiguration Parse(string json, string runId)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                throw new RunConfigurationException($"not valid JSON: {exception.Message}");
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                Fields(root, "", "epochUtc", "fixedStepSeconds", "randomSeed", "kernelSetId", "initialOrbit", "spacecraft", "sensors");

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
                SpacecraftConfiguration spacecraft = Spacecraft(Field(root, "spacecraft", ""), "spacecraft");
                SensorConfiguration[] sensors = Sensors(Field(root, "sensors", ""), "sensors");

                SimulationConfiguration configuration = new SimulationConfiguration(
                    runId,
                    epochUtc,
                    fixedStepSeconds,
                    initialOrbit: orbit,
                    randomSeed: randomSeed,
                    kernelSetId: kernelSetId,
                    spacecraft: spacecraft,
                    sensors: sensors);
                Check(configuration.IsValid, "", "is not a valid run configuration");
                return configuration;
            }
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

        private static SpacecraftConfiguration Spacecraft(JsonElement element, string path)
        {
            Fields(
                element,
                path,
                "massKilograms",
                "inertiaBodyKilogramSquareMeters",
                "initialBodyToJ2000",
                "initialAngularVelocityBodyRadiansPerSecond");
            SpacecraftConfiguration spacecraft = new SpacecraftConfiguration(
                Number(element, "massKilograms", path),
                Matrix(element, "inertiaBodyKilogramSquareMeters", path),
                Quaternion(element, "initialBodyToJ2000", path),
                Vector(element, "initialAngularVelocityBodyRadiansPerSecond", path));
            Check(spacecraft.IsValid, path, "needs mass > 0, a symmetric positive-definite inertia, a unit quaternion and a finite rate");
            return spacecraft;
        }

        private static SensorConfiguration[] Sensors(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw Error(path, "must be an array");
            }

            SensorConfiguration[] sensors = new SensorConfiguration[element.GetArrayLength()];
            HashSet<string> sensorIds = new HashSet<string>(StringComparer.Ordinal);
            int index = 0;
            foreach (JsonElement sensor in element.EnumerateArray())
            {
                string sensorPath = $"{path}[{index}]";
                sensors[index] = Sensor(sensor, sensorPath);
                Check(sensorIds.Add(sensors[index].Definition.SensorId), Join(sensorPath, "sensorId"), "duplicates another sensor's ID");
                index++;
            }

            return sensors;
        }

        // One sensor: the SensorDefinition fields plus exactly one profile, named by its kind.
        private static SensorConfiguration Sensor(JsonElement element, string path)
        {
            Fields(
                element,
                path,
                new[] { "sensorId", "modelName", "frameId", "samplePeriodSeconds", "source", "controllerVisible", "mount" },
                new[] { "imu", "magnetometer", "lightSensor", "camera" });

            JsonElement mount = Field(element, "mount", path);
            string mountPath = Join(path, "mount");
            Fields(mount, mountPath, "positionBodyMeters", "sensorToBody");
            SensorMount sensorMount = new SensorMount(
                Vector(mount, "positionBodyMeters", mountPath),
                Quaternion(mount, "sensorToBody", mountPath));
            Check(sensorMount.IsValid, mountPath, "needs a finite position and a unit quaternion");

            SensorDefinition definition = new SensorDefinition(
                Text(element, "sensorId", path),
                Text(element, "modelName", path),
                Text(element, "frameId", path),
                Number(element, "samplePeriodSeconds", path),
                Text(element, "source", path),
                sensorMount,
                Boolean(element, "controllerVisible", path));
            Check(definition.IsValid, path, "needs non-blank sensorId, modelName, frameId and source, and samplePeriodSeconds > 0");

            SensorConfiguration configuration;
            string profilePath;
            if (element.TryGetProperty("imu", out JsonElement imu))
            {
                profilePath = Join(path, "imu");
                Fields(imu, profilePath, "gyroscope", "accelerometer");
                configuration = SensorConfiguration.ForImu(
                    definition,
                    new ImuProfile(
                        Gyroscope(Field(imu, "gyroscope", profilePath), Join(profilePath, "gyroscope")),
                        Accelerometer(Field(imu, "accelerometer", profilePath), Join(profilePath, "accelerometer"))));
            }
            else if (element.TryGetProperty("magnetometer", out JsonElement magnetometer))
            {
                profilePath = Join(path, "magnetometer");
                Fields(magnetometer, profilePath, "noiseStdTesla", "constantBiasSensorTesla", "scaleFactor", "rangeTesla");
                configuration = SensorConfiguration.ForMagnetometer(
                    definition,
                    new MagnetometerProfile(
                        Number(magnetometer, "noiseStdTesla", profilePath),
                        Vector(magnetometer, "constantBiasSensorTesla", profilePath),
                        Number(magnetometer, "scaleFactor", profilePath),
                        Number(magnetometer, "rangeTesla", profilePath)));
            }
            else if (element.TryGetProperty("camera", out JsonElement camera))
            {
                // Argus computes cameras, so the host never sends them to Basilisk (CORE-18).
                profilePath = Join(path, "camera");
                Fields(camera, profilePath, "intrinsics", "exposureDurationSeconds");
                configuration = SensorConfiguration.ForCamera(
                    definition,
                    new CameraProfile(
                        Intrinsics(Field(camera, "intrinsics", profilePath), Join(profilePath, "intrinsics")),
                        Number(camera, "exposureDurationSeconds", profilePath)));
            }
            else
            {
                JsonElement light = Field(element, "lightSensor", path);
                profilePath = Join(path, "lightSensor");
                Fields(
                    light,
                    profilePath,
                    "fieldOfViewHalfAngleRadians",
                    "scaleFactor",
                    "noiseStdWattsPerSquareMeter",
                    "biasWattsPerSquareMeter",
                    "saturationWattsPerSquareMeter");
                configuration = SensorConfiguration.ForLightSensor(
                    definition,
                    new LightSensorProfile(
                        Number(light, "fieldOfViewHalfAngleRadians", profilePath),
                        Number(light, "scaleFactor", profilePath),
                        Number(light, "noiseStdWattsPerSquareMeter", profilePath),
                        Number(light, "biasWattsPerSquareMeter", profilePath),
                        Number(light, "saturationWattsPerSquareMeter", profilePath)));
            }

            Check(configuration.IsValid, profilePath, "is not a valid profile (see the Core profile's IsValid)");
            return configuration;
        }

        private static GyroscopeProfile Gyroscope(JsonElement element, string path)
        {
            Fields(
                element,
                path,
                "noiseDensityRadiansPerSecondPerRootHertz",
                "biasRandomWalkRadiansPerSecondPerRootSecond",
                "biasRandomWalkBoundRadiansPerSecond",
                "constantBiasSensorRadiansPerSecond",
                "scaleFactorPerAxis",
                "rangeRadiansPerSecond",
                "resolutionRadiansPerSecond");
            GyroscopeProfile profile = new GyroscopeProfile(
                Number(element, "noiseDensityRadiansPerSecondPerRootHertz", path),
                Number(element, "biasRandomWalkRadiansPerSecondPerRootSecond", path),
                Number(element, "biasRandomWalkBoundRadiansPerSecond", path),
                Vector(element, "constantBiasSensorRadiansPerSecond", path),
                Vector(element, "scaleFactorPerAxis", path),
                Number(element, "rangeRadiansPerSecond", path),
                Number(element, "resolutionRadiansPerSecond", path));
            Check(profile.IsValid, path, "is not a valid gyroscope profile (see GyroscopeProfile.IsValid)");
            return profile;
        }

        private static AccelerometerProfile Accelerometer(JsonElement element, string path)
        {
            Fields(
                element,
                path,
                "noiseDensityMetersPerSecondSquaredPerRootHertz",
                "biasRandomWalkMetersPerSecondSquaredPerRootSecond",
                "biasRandomWalkBoundMetersPerSecondSquared",
                "constantBiasSensorMetersPerSecondSquared",
                "scaleFactorPerAxis",
                "rangeMetersPerSecondSquared",
                "resolutionMetersPerSecondSquared");
            AccelerometerProfile profile = new AccelerometerProfile(
                Number(element, "noiseDensityMetersPerSecondSquaredPerRootHertz", path),
                Number(element, "biasRandomWalkMetersPerSecondSquaredPerRootSecond", path),
                Number(element, "biasRandomWalkBoundMetersPerSecondSquared", path),
                Vector(element, "constantBiasSensorMetersPerSecondSquared", path),
                Vector(element, "scaleFactorPerAxis", path),
                Number(element, "rangeMetersPerSecondSquared", path),
                Number(element, "resolutionMetersPerSecondSquared", path));
            Check(profile.IsValid, path, "is not a valid accelerometer profile (see AccelerometerProfile.IsValid)");
            return profile;
        }

        private static CameraIntrinsics Intrinsics(JsonElement element, string path)
        {
            Fields(
                element,
                path,
                "widthPixels",
                "heightPixels",
                "focalLengthXPixels",
                "focalLengthYPixels",
                "principalPointXPixels",
                "principalPointYPixels");
            CameraIntrinsics intrinsics = new CameraIntrinsics(
                Integer(element, "widthPixels", path),
                Integer(element, "heightPixels", path),
                Number(element, "focalLengthXPixels", path),
                Number(element, "focalLengthYPixels", path),
                Number(element, "principalPointXPixels", path),
                Number(element, "principalPointYPixels", path));
            Check(intrinsics.IsValid, path, "needs a width and height > 0 and finite focal lengths > 0 (see CameraIntrinsics.IsValid)");
            return intrinsics;
        }

        // An object with exactly these fields.
        private static void Fields(JsonElement element, string path, params string[] required) =>
            Fields(element, path, required, Array.Empty<string>());

        // An object with every required field, exactly one of the oneOf fields when any are listed,
        // and nothing else.
        private static void Fields(JsonElement element, string path, string[] required, string[] oneOf)
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
                else if (Array.IndexOf(required, property.Name) < 0)
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
        private static Quaterniond Quaternion(JsonElement element, string name, string path)
        {
            JsonElement value = Field(element, name, path);
            string quaternionPath = Join(path, name);
            Fields(value, quaternionPath, "x", "y", "z", "w");
            return new Quaterniond(
                Number(value, "x", quaternionPath),
                Number(value, "y", quaternionPath),
                Number(value, "z", quaternionPath),
                Number(value, "w", quaternionPath));
        }

        // Three rows, each [x, y, z].
        private static Matrix3d Matrix(JsonElement element, string name, string path)
        {
            JsonElement value = Field(element, name, path);
            string matrixPath = Join(path, name);
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
