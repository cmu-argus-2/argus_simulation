using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Argus.Simulation.Core;

namespace Argus.Simulation.Host
{
    // One dataset v1 run directory (<run_id>_<segment>, Core/Recording/README.md), read back into the
    // Core contracts the state stream publishes (HOST-7). It reads what the stream carries: the
    // manifest's devices, every state, and every non-camera sensor frame. commands.jsonl, the images
    // and files it does not know stay unread. A bad file or line throws InvalidDataException naming
    // it.
    internal sealed class RecordedRun
    {
        public const string SchemaVersion = "argus-dataset/1";
        private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

        // Every device the manifest lists, configured or not: a frame of any other sensor is refused.
        private readonly HashSet<string> _declared;

        private RecordedRun(
            string directory,
            bool complete,
            string dynamicsBackend,
            ReferenceFrame earthFixedFrame,
            SimulationConfiguration configuration,
            HashSet<string> declared)
        {
            _declared = declared;
            Directory = directory;
            Complete = complete;
            DynamicsBackend = dynamicsBackend;
            EarthFixedFrame = earthFixedFrame;
            Configuration = configuration;
        }

        public string Directory { get; }

        // False for a recording that crashed or is still being written: it is read up to its last
        // full line.
        public bool Complete { get; }

        public string DynamicsBackend { get; }
        public ReferenceFrame EarthFixedFrame { get; }

        // The run ID, epoch, step and configured devices; the orbit and body are not read, since
        // nothing replayed uses them.
        public SimulationConfiguration Configuration { get; }

        public string RunId => Configuration.RunId;

        public static RecordedRun Open(string directory)
        {
            string path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"{directory} has no manifest.json; --replay takes one <run_id>_<segment> directory.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
                JsonElement manifest = document.RootElement;
                string schema = Text(manifest, "schema_version");
                if (schema != SchemaVersion)
                {
                    throw new FormatException($"schema_version '{schema}' is not {SchemaVersion}");
                }

                string runId = Text(manifest, "run_id");
                JsonElement configuration = Field(manifest, "configuration");
                DateTimeOffset epochUtc = Utc(configuration, "epoch_utc");
                double fixedStepSeconds = Number(configuration, "fixed_step_s");
                HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
                DeviceConfiguration[] devices = Devices(Field(manifest, "devices"), declared);
                SimulationConfiguration run = new SimulationConfiguration(runId, epochUtc, fixedStepSeconds, devices: devices);
                if (!run.IsValid)
                {
                    // Named as RunConfigurationFile names a bad device: the first one invalid on its own.
                    foreach (DeviceConfiguration device in devices)
                    {
                        if (!new SimulationConfiguration(runId, epochUtc, fixedStepSeconds, devices: new[] { device }).IsValid)
                        {
                            throw new FormatException(
                                $"device '{device.Definition.SensorId}' is not a valid {device.Kind} for a {fixedStepSeconds:R} s step: " +
                                (device.FindProblem() ?? "its definition (sample period, mount, source or visibility) is not valid"));
                        }
                    }

                    throw new FormatException("configuration (run_id, fixed_step_s and devices) is not a valid SimulationConfiguration");
                }

                return new RecordedRun(
                    directory,
                    Flag(manifest, "complete"),
                    Text(Field(manifest, "dynamics_backend"), "name"),
                    Enum<ReferenceFrame>(manifest, "earth_fixed_frame"),
                    run,
                    declared);
            }
            catch (Exception exception) when (exception is JsonException || exception is FormatException)
            {
                throw new InvalidDataException($"{path}: {exception.Message}", exception);
            }
        }

        // Each state with the step's non-camera frames, in file order: the output a live run's
        // gateway raised for that step, as far as the stream carries it. The backend sensors' frames
        // also rebuild the state's backend measurements, since a backend sensor publishes a frame
        // exactly when it was sampled (D10). Applies the dataset README's reader checks to what it
        // reads.
        public IEnumerable<(SimulationState State, SensorOutputSet Output)> ReadSteps()
        {
            Dictionary<string, DeviceConfiguration> streamed = new Dictionary<string, DeviceConfiguration>(StringComparer.Ordinal);
            foreach (DeviceConfiguration device in Configuration.Devices)
            {
                if (device.KindDefinition.Role == DeviceRole.Sensor && !(device.KindDefinition is ICameraKind))
                {
                    streamed.Add(device.Definition.SensorId, device);
                }
            }

            Dictionary<string, long> frameSequences = new Dictionary<string, long>(StringComparer.Ordinal);
            using JsonLines states = new JsonLines(Directory, "states.jsonl", Complete);
            using JsonLines sensors = new JsonLines(Directory, "sensors.jsonl", Complete);
            long lastSequence = -1;
            while (states.Next())
            {
                SimulationState truth = State(states);
                long sequence = truth.Spacecraft.Sequence;
                if (sequence <= lastSequence)
                {
                    throw states.Error($"sequence {sequence} does not increase");
                }

                lastSequence = sequence;
                List<ISensorFrame> frames = new List<ISensorFrame>();
                Dictionary<string, object> measurements = new Dictionary<string, object>(StringComparer.Ordinal);
                List<string> unavailable = new List<string>();
                // A non-camera frame's state_sequence is the step that published it, so the frames
                // of state k come before any frame of a later state. Camera frames, a late one
                // included, never reach the stream.
                while (sensors.Peek())
                {
                    (string sensorId, long stateSequence) = Envelope(sensors);
                    if (!streamed.TryGetValue(sensorId, out DeviceConfiguration device))
                    {
                        // A camera, or a listed sensor with no kind: the stream carries neither.
                        if (!_declared.Contains(sensorId))
                        {
                            throw sensors.Error($"sensor '{sensorId}' is not a device the manifest lists");
                        }

                        sensors.Take();
                        continue;
                    }

                    if (stateSequence > sequence)
                    {
                        break;
                    }

                    ISensorFrame frame = Frame(sensors, device);
                    sensors.Take();
                    if (frame == null || stateSequence < sequence)
                    {
                        // An unknown payload type, or a frame whose state was dropped (overflow).
                        continue;
                    }

                    if (frameSequences.TryGetValue(sensorId, out long previous) && frame.Sequence <= previous)
                    {
                        throw sensors.Error($"sensor '{sensorId}' sequence {frame.Sequence} does not increase");
                    }

                    frameSequences[sensorId] = frame.Sequence;
                    frames.Add(frame);
                    if (device.IsBackendSensor)
                    {
                        if (frame.Status == SensorFrameStatus.Unavailable)
                        {
                            unavailable.Add(sensorId);
                        }
                        else
                        {
                            measurements[sensorId] = frame.UntypedPayload;
                        }
                    }
                }

                SimulationState state = new SimulationState(
                    truth.RunId,
                    truth.DynamicsBackend,
                    truth.Spacecraft,
                    truth.AppliedCommands,
                    truth.Environment,
                    new SensorMeasurementSet(measurements, unavailable));
                if (!state.IsValid)
                {
                    throw states.Error($"state {sequence} is not a valid SimulationState");
                }

                yield return (state, new SensorOutputSet(state.RunId, sequence, state.Spacecraft.SimulationTimeSeconds, frames));
            }
        }

        private static (string SensorId, long StateSequence) Envelope(JsonLines lines)
        {
            try
            {
                return (Text(lines.Line, "sensor_id"), Integer(lines.Line, "state_sequence"));
            }
            catch (FormatException exception)
            {
                throw lines.Error(exception.Message);
            }
        }

        private SimulationState State(JsonLines lines)
        {
            JsonElement line = lines.Line;
            try
            {
                string runId = Text(line, "run_id");
                if (runId != RunId)
                {
                    throw new FormatException($"run_id '{runId}' is not the manifest's '{RunId}'");
                }

                SpacecraftState spacecraft = new SpacecraftState(
                    Integer(line, "sequence"),
                    Number(line, "simulation_time_s"),
                    Utc(line, "timestamp_utc"),
                    Vector(line, "position_ecef_m"),
                    Vector(line, "velocity_ecef_m_s"),
                    Quaternion(line, "body_to_ecef_xyzw"),
                    Vector(line, "angular_velocity_body_rad_s"),
                    EarthFixedFrame);
                JsonElement command = Field(line, "producing_command");
                JsonElement environment = Field(line, "environment");
                return new SimulationState(
                    runId,
                    DynamicsBackend,
                    spacecraft,
                    IsNull(command) ? (DeviceCommandSet?)null : Command(command),
                    IsNull(environment)
                        ? (EnvironmentState?)null
                        : new EnvironmentState(
                            Vector(environment, "sun_position_itrf93_m"),
                            Quaternion(environment, "j2000_to_itrf93_xyzw"),
                            Vector(environment, "earth_angular_velocity_itrf93_rad_s"),
                            Number(environment, "spacecraft_shadow_factor")));
            }
            catch (Exception exception) when (exception is FormatException || exception is InvalidOperationException)
            {
                throw lines.Error(exception.Message);
            }
        }

        // The frame as the sensor published it, or null for a payload type the reader does not know.
        private ISensorFrame Frame(JsonLines lines, DeviceConfiguration device)
        {
            JsonElement line = lines.Line;
            try
            {
                SensorFrameStatus status = Enum<SensorFrameStatus>(line, "status");
                JsonElement payload = Field(line, "payload");
                // A Valid frame's payload has no null number; an Invalid one keeps the model's
                // non-finite values as null (the dataset README's Numbers row).
                bool finite = status == SensorFrameStatus.Valid;
                bool none = IsNull(payload);
                if (none && status != SensorFrameStatus.Unavailable)
                {
                    throw new FormatException($"a {status} frame has no payload");
                }

                switch (Text(line, "payload_type"))
                {
                    case nameof(ImuMeasurement):
                        return Frame(line, status, none ? default : new ImuMeasurement(
                            Vector(payload, "angular_velocity_sensor_rad_s", finite),
                            Vector(payload, "specific_force_sensor_m_s2", finite)));
                    case nameof(AngularRateMeasurement):
                        return Frame(line, status, none ? default : new AngularRateMeasurement(
                            Vector(payload, "angular_velocity_sensor_rad_s", finite)));
                    case nameof(MagnetometerMeasurement):
                        return Frame(line, status, none ? default : new MagnetometerMeasurement(
                            Vector(payload, "magnetic_field_sensor_t", finite)));
                    case nameof(LightSensorMeasurement):
                        return Frame(line, status, none ? default : new LightSensorMeasurement(
                            Number(payload, "irradiance_w_m2", finite)));
                    default:
                        return null;
                }
            }
            catch (Exception exception) when (exception is FormatException || exception is InvalidOperationException)
            {
                throw lines.Error(exception.Message);
            }
        }

        private SensorFrame<T> Frame<T>(JsonElement line, SensorFrameStatus status, T payload)
        {
            string runId = Text(line, "run_id");
            if (runId != RunId)
            {
                throw new FormatException($"run_id '{runId}' is not the manifest's '{RunId}'");
            }

            return new SensorFrame<T>(
                runId,
                Text(line, "sensor_id"),
                Text(line, "frame_id"),
                Integer(line, "sequence"),
                Integer(line, "state_sequence"),
                Number(line, "simulation_time_s"),
                Utc(line, "timestamp_utc"),
                status,
                Text(line, "source"),
                payload);
        }

        // The manifest's configured devices, in its order (sensors, then actuators), adding every
        // listed device ID to declared. A sensor with no kind, such as the ideal body-rate sensor,
        // has no configuration to rebuild.
        private static DeviceConfiguration[] Devices(JsonElement devices, HashSet<string> declared)
        {
            List<DeviceConfiguration> configured = new List<DeviceConfiguration>();
            foreach (JsonElement device in Elements(devices, "devices"))
            {
                string deviceId = Text(device, "device_id");
                if (!declared.Add(deviceId))
                {
                    throw new FormatException($"device '{deviceId}' is listed twice");
                }

                JsonElement kind = Field(device, "kind");
                if (IsNull(kind))
                {
                    continue;
                }

                string kindName = kind.GetString();
                if (DeviceKinds.Find(kindName) == null)
                {
                    throw new FormatException($"device '{deviceId}': '{kindName}' is not a registered device kind ({DeviceKinds.Names})");
                }

                JsonElement mount = Field(device, "mount");
                configured.Add(new DeviceConfiguration(
                    new SensorDefinition(
                        deviceId,
                        Text(device, "model_name"),
                        Text(device, "frame_id"),
                        Number(device, "sample_period_s"),
                        Text(device, "source"),
                        new SensorMount(Vector(mount, "position_body_m"), Quaternion(mount, "device_to_body_xyzw")),
                        Flag(device, "controller_visible")),
                    kindName,
                    Values(Field(device, "parameters"), true),
                    Values(Field(device, "initial_state"), true)));
            }

            return configured.ToArray();
        }

        private static DeviceCommandSet Command(JsonElement command)
        {
            List<DeviceCommand> devices = new List<DeviceCommand>();
            JsonElement map = Field(command, "devices");
            if (map.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("producing_command.devices is not an object");
            }

            foreach (JsonProperty device in map.EnumerateObject())
            {
                devices.Add(new DeviceCommand(device.Name, Values(device.Value, true)));
            }

            return new DeviceCommandSet(Integer(command, "sequence"), Number(command, "apply_at_simulation_time_s"), devices);
        }

        // A device value map in the manifest's value forms (docs/device-model.md §3): a number,
        // [x, y, z], three rows, a quaternion {"x", "y", "z", "w"}, a bool or a string.
        private static DeviceValues Values(JsonElement element, bool finite)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("a device value map is not an object");
            }

            DeviceValues.Builder values = new DeviceValues.Builder();
            foreach (JsonProperty entry in element.EnumerateObject())
            {
                JsonElement value = entry.Value;
                switch (value.ValueKind)
                {
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        values.Add(entry.Name, DeviceValue.FromFlag(value.GetBoolean()));
                        break;
                    case JsonValueKind.String:
                        values.Add(entry.Name, DeviceValue.FromText(value.GetString()));
                        break;
                    case JsonValueKind.Object:
                        values.Add(entry.Name, DeviceValue.FromQuaternion(new Quaterniond(
                            Number(value, "x", finite), Number(value, "y", finite), Number(value, "z", finite), Number(value, "w", finite))));
                        break;
                    case JsonValueKind.Array when value.GetArrayLength() == 3 && value[0].ValueKind == JsonValueKind.Array:
                        values.Add(entry.Name, DeviceValue.FromMatrix3(new Matrix3d(
                            VectorValue(value[0], entry.Name, finite), VectorValue(value[1], entry.Name, finite), VectorValue(value[2], entry.Name, finite))));
                        break;
                    case JsonValueKind.Array:
                        values.Add(entry.Name, DeviceValue.FromVector3(VectorValue(value, entry.Name, finite)));
                        break;
                    default:
                        values.Add(entry.Name, DeviceValue.FromNumber(NumberValue(value, entry.Name, finite)));
                        break;
                }
            }

            return values.Build();
        }

        private static JsonElement Field(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"'{name}' is read from a value that is not an object");
            }

            // A missing key reads as null (the dataset README's Keys row): see IsNull.
            return element.TryGetProperty(name, out JsonElement value) ? value : default;
        }

        // null, or a key that is missing (Undefined).
        private static bool IsNull(JsonElement value) =>
            value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined;

        private static IEnumerable<JsonElement> Elements(JsonElement array, string name)
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"{name} is not an array");
            }

            return array.EnumerateArray();
        }

        private static string Text(JsonElement element, string name)
        {
            JsonElement value = Field(element, name);
            return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"{name} is not a string");
        }

        private static bool Flag(JsonElement element, string name)
        {
            JsonElement value = Field(element, name);
            return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False
                ? value.GetBoolean()
                : throw new FormatException($"{name} is not true or false");
        }

        private static long Integer(JsonElement element, string name)
        {
            JsonElement value = Field(element, name);
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
                ? result
                : throw new FormatException($"{name} is not an integer");
        }

        private static double Number(JsonElement element, string name, bool finite = true) =>
            NumberValue(Field(element, name), name, finite);

        // null is a non-finite value written as null; allowed only where the value may be
        // non-finite.
        private static double NumberValue(JsonElement value, string name, bool finite)
        {
            if (IsNull(value) && !finite)
            {
                return double.NaN;
            }

            return value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : throw new FormatException($"{name} is not a number");
        }

        private static Vector3d Vector(JsonElement element, string name, bool finite = true) =>
            VectorValue(Field(element, name), name, finite);

        private static Vector3d VectorValue(JsonElement value, string name, bool finite)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            {
                throw new FormatException($"{name} is not [x, y, z]");
            }

            return new Vector3d(NumberValue(value[0], name, finite), NumberValue(value[1], name, finite), NumberValue(value[2], name, finite));
        }

        private static Quaterniond Quaternion(JsonElement element, string name)
        {
            JsonElement value = Field(element, name);
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
            {
                throw new FormatException($"{name} is not [x, y, z, w]");
            }

            return new Quaterniond(
                NumberValue(value[0], name, true), NumberValue(value[1], name, true), NumberValue(value[2], name, true), NumberValue(value[3], name, true));
        }

        private static DateTimeOffset Utc(JsonElement element, string name) =>
            DateTimeOffset.TryParseExact(
                Text(element, name),
                UtcFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset value)
                ? value
                : throw new FormatException($"{name} is not yyyy-MM-ddTHH:mm:ss.fffffffZ");

        private static T Enum<T>(JsonElement element, string name)
            where T : struct
        {
            string text = Text(element, name);
            // The C# member's exact spelling (the dataset README's Enums row), never a number.
            return text.Length > 0 && char.IsLetter(text[0]) && System.Enum.TryParse(text, false, out T value) &&
                System.Enum.IsDefined(typeof(T), value)
                ? value
                : throw new FormatException($"{name} '{text}' is not a {typeof(T).Name}");
        }

        // One JSONL file, read one line ahead. In an incomplete recording, a last line cut short
        // ends the file instead of failing it.
        private sealed class JsonLines : IDisposable
        {
            private readonly StreamReader _reader;
            private readonly string _name;
            private readonly bool _complete;
            private JsonDocument _document;
            private bool _peeked;
            private long _number;

            public JsonLines(string directory, string name, bool complete)
            {
                string path = Path.Combine(directory, name);
                if (!File.Exists(path))
                {
                    throw new InvalidDataException($"{directory} has no {name}.");
                }

                _reader = new StreamReader(path);
                _name = name;
                _complete = complete;
            }

            public JsonElement Line => _document.RootElement;

            // Reads the next line, or returns false at the end.
            public bool Next()
            {
                Take();
                return Peek();
            }

            // Reads the next line once, keeping it until Take; false at the end.
            public bool Peek()
            {
                if (_peeked)
                {
                    return _document != null;
                }

                _peeked = true;
                string text = _reader.ReadLine();
                if (text == null)
                {
                    return false;
                }

                _number++;
                try
                {
                    _document = JsonDocument.Parse(text);
                }
                catch (JsonException exception)
                {
                    if (!_complete && _reader.Peek() < 0)
                    {
                        return false;
                    }

                    throw Error(exception.Message);
                }

                if (_document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw Error("is not a JSON object");
                }

                return true;
            }

            // Consumes the line Peek read.
            public void Take()
            {
                _document?.Dispose();
                _document = null;
                _peeked = false;
            }

            public InvalidDataException Error(string message) => new InvalidDataException($"{_name} line {_number}: {message}");

            public void Dispose()
            {
                _document?.Dispose();
                _reader.Dispose();
            }
        }
    }
}
