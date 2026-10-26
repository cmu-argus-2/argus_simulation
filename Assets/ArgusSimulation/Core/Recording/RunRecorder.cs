using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Argus.Simulation.Core
{
    // Writes dataset v1 (README.md in this folder) from every SimulationState and SensorOutputSet
    // (D6). The calls only queue the data; one writer thread formats, encodes PNGs and writes files,
    // so a slow or stalled disk never delays the step loop. The queue holds at most Capacity states
    // and sensor output sets. When it is full the newest one is dropped, and the manifest's overflow
    // entry counts every dropped state and frame. Each BeginRun opens <root>/<run_id>_<segment>.
    public sealed class RunRecorder : IRunRecorder
    {
        public const string SchemaVersion = "argus-dataset/1";
        public const string RecorderVersion = "0.1.0";
        public const string OverflowPolicy = "drop-newest";
        // TODO(#99): bound the queue by bytes as well as items before camera frames arrive. 65,536
        // queued ~1 MB images is ~64 GB, and ImageFrame.Data is queued by reference, so a renderer
        // that reuses its readback buffer would change frames already queued.
        public const int DefaultCapacity = 65536;

        private const string StatesFile = "states.jsonl";
        private const string SensorsFile = "sensors.jsonl";
        private const string ManifestFile = "manifest.json";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly string _rootDirectory;
        private readonly string _process;
        private readonly string _commit;
        private readonly int _capacity;
        private readonly Action _beforeEachItem;
        private readonly object _gate = new object();
        private readonly Queue<Item> _queue = new Queue<Item>();
        private readonly Thread _writer;

        // Guarded by _gate.
        private int _queuedData;
        private DropItem _openDrop;
        private bool _closing;
        private bool _hasRun;
        private readonly Dictionary<string, int> _lastSegments = new Dictionary<string, int>(StringComparer.Ordinal);

        // Writer thread only, then read after it has been joined; _failure is also read by Failure.
        private Segment _current;
        private volatile Exception _failure;

        // process is manifest.writer.process (headless-host or unity); commit is the full
        // argus_simulation hash, or null when unknown.
        public RunRecorder(string rootDirectory, string process, string commit = null, int capacity = DefaultCapacity)
            : this(rootDirectory, process, commit, capacity, null)
        {
        }

        // beforeEachItem runs on the writer thread before each queued item; tests use it to stall
        // the writer.
        internal RunRecorder(string rootDirectory, string process, string commit, int capacity, Action beforeEachItem)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("A root directory is required.", nameof(rootDirectory));
            }

            if (string.IsNullOrWhiteSpace(process))
            {
                throw new ArgumentException("The writer process name is required.", nameof(process));
            }

            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "The queue capacity must be >= 1.");
            }

            _rootDirectory = Path.GetFullPath(rootDirectory);
            ThrowUnlessWritable(_rootDirectory);
            _process = process;
            _commit = IsCommitHash(commit) ? commit : null;
            _capacity = capacity;
            _beforeEachItem = beforeEachItem;
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Argus RunRecorder" };
            _writer.Start();
        }

        public int Capacity => _capacity;

        // Set once the writer has failed; everything queued after it is dropped. A caller that polls
        // it between steps stops the run instead of running on with nothing written.
        public Exception Failure => _failure;

        // [a-z0-9][a-z0-9._-]*, not ending in '.', and not a Windows device name: the rule for run
        // and sensor IDs, which become directory names on every OS (Recording/README.md).
        public static bool IsPathSafeId(string id)
        {
            if (!SimulationConfiguration.IsLowercaseIdentifier(id) || id[id.Length - 1] == '.')
            {
                return false;
            }

            int dot = id.IndexOf('.');
            string stem = dot < 0 ? id : id.Substring(0, dot);
            switch (stem)
            {
                case "con":
                case "prn":
                case "aux":
                case "nul":
                    return false;
                default:
                    return !((stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal)) &&
                        stem.Length == 4 && stem[3] >= '0' && stem[3] <= '9');
            }
        }

        // Fails here, before a run connects, rather than at Close after the run.
        private static void ThrowUnlessWritable(string rootDirectory)
        {
            try
            {
                Directory.CreateDirectory(rootDirectory);
                string probe = Path.Combine(rootDirectory, ".argus-write-probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                throw new IOException($"Cannot write datasets under {rootDirectory}: {exception.Message}", exception);
            }
        }

        // The commit in an assembly's informational version ("1.0.0+<hash>", which the .NET SDK
        // adds from git), or null.
        public static string CommitOf(Assembly assembly)
        {
            string version = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            int separator = version?.LastIndexOf('+') ?? -1;
            string commit = separator < 0 ? null : version.Substring(separator + 1);
            return IsCommitHash(commit) ? commit : null;
        }

        public void BeginRun(
            SimulationConfiguration run,
            IReadOnlyList<SensorDefinition> sensors,
            string dynamicsBackendVersion = null,
            AnalyticOrbit? analyticOrbit = null)
        {
            if (!run.IsValid || !IsPathSafeId(run.RunId))
            {
                throw new ArgumentException("A valid run whose ID is usable as a directory name is required.", nameof(run));
            }

            if (sensors == null)
            {
                throw new ArgumentNullException(nameof(sensors));
            }

            SensorDefinition[] definitions = new SensorDefinition[sensors.Count];
            for (int i = 0; i < definitions.Length; i++)
            {
                definitions[i] = sensors[i];
                if (!IsPathSafeId(definitions[i].SensorId))
                {
                    throw new ArgumentException(
                        $"Sensor ID '{definitions[i].SensorId}' is not path-safe ([a-z0-9][a-z0-9._-]*, see Recording/README.md).",
                        nameof(sensors));
                }
            }

            lock (_gate)
            {
                ThrowIfClosing();
                // The next segment of this run ID, never one already on disk (from an earlier
                // process, or an earlier run with the same ID): the recorder never overwrites.
                int segment = _lastSegments.TryGetValue(run.RunId, out int last) ? last + 1 : 0;
                while (Directory.Exists(Path.Combine(_rootDirectory, run.RunId + "_" + segment.ToString(CultureInfo.InvariantCulture))))
                {
                    segment++;
                }

                _lastSegments[run.RunId] = segment;
                _hasRun = true;
                Push(new BeginItem(run, definitions, dynamicsBackendVersion, analyticOrbit, segment, DateTimeOffset.UtcNow));
            }
        }

        public void RecordState(SimulationState state) => Enqueue(new StateItem(state), 1, 0);

        // Never dropped, even when the queue is full: the manifest must say why a run ended.
        public void RecordTermination(Observation terminal)
        {
            if (terminal == null || !terminal.IsTerminal)
            {
                throw new ArgumentException("A terminal observation is required.", nameof(terminal));
            }

            lock (_gate)
            {
                ThrowIfClosing();
                if (!_hasRun)
                {
                    throw new InvalidOperationException("Call BeginRun before recording a run.");
                }

                Push(new TerminationItem(terminal));
            }
        }

        public void RecordSensorOutput(SensorOutputSet output)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            Enqueue(new OutputItem(output), 0, output.Frames.Count);
        }

        public void Close()
        {
            lock (_gate)
            {
                _closing = true;
                Monitor.PulseAll(_gate);
            }

            _writer.Join();
            if (_failure != null)
            {
                throw new InvalidOperationException($"The run recorder failed: {_failure.Message}", _failure);
            }
        }

        public void Dispose()
        {
            try
            {
                Close();
            }
            catch (InvalidOperationException)
            {
                // The manifest stays "complete": false; Close reports the failure to callers that ask.
            }
        }

        private void Enqueue(Item item, int states, int frames)
        {
            lock (_gate)
            {
                ThrowIfClosing();
                if (!_hasRun)
                {
                    throw new InvalidOperationException("Call BeginRun before recording a run.");
                }

                if (_queuedData >= _capacity)
                {
                    if (_openDrop == null)
                    {
                        Push(new DropItem());
                    }

                    _openDrop.States += states;
                    _openDrop.Frames += frames;
                    return;
                }

                _queuedData++;
                Push(item);
            }
        }

        // Under _gate. Consecutive drops share one DropItem until something else is queued.
        private void Push(Item item)
        {
            _openDrop = item as DropItem;
            _queue.Enqueue(item);
            Monitor.PulseAll(_gate);
        }

        private void ThrowIfClosing()
        {
            if (_closing)
            {
                throw new InvalidOperationException("The run recorder is closed.");
            }
        }

        private void WriteLoop()
        {
            while (true)
            {
                Item item;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_closing)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_queue.Count == 0)
                    {
                        break;
                    }

                    item = _queue.Dequeue();
                    if (item == _openDrop)
                    {
                        _openDrop = null;
                    }

                    if (item is StateItem || item is OutputItem)
                    {
                        _queuedData--;
                    }
                }

                _beforeEachItem?.Invoke();
                if (_failure == null)
                {
                    try
                    {
                        Write(item);
                    }
                    catch (Exception exception)
                    {
                        // Later items are drained unwritten; the manifest stays "complete": false.
                        _failure = exception;
                        _current?.Abandon();
                        _current = null;
                    }
                }
            }

            if (_failure == null && _current != null)
            {
                try
                {
                    _current.Finish(this);
                }
                catch (Exception exception)
                {
                    _failure = exception;
                    _current.Abandon();
                }
            }
        }

        private void Write(Item item)
        {
            switch (item)
            {
                case BeginItem begin:
                    _current?.Finish(this);
                    _current = null;
                    _current = new Segment(Path.Combine(_rootDirectory, begin.Run.RunId + "_" + begin.Segment.ToString(CultureInfo.InvariantCulture)), begin);
                    break;
                case StateItem state:
                    _current.Write(this, state.State);
                    break;
                case OutputItem output:
                    _current.Write(output.Output);
                    break;
                case TerminationItem termination:
                    _current.Termination = termination.Observation;
                    break;
                case DropItem drop:
                    _current.DroppedStates += drop.States;
                    _current.DroppedFrames += drop.Frames;
                    break;
            }
        }

        private static bool IsCommitHash(string value)
        {
            if (value == null || value.Length != 40)
            {
                return false;
            }

            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        private abstract class Item
        {
        }

        private sealed class BeginItem : Item
        {
            public BeginItem(
                SimulationConfiguration run,
                SensorDefinition[] sensors,
                string dynamicsBackendVersion,
                AnalyticOrbit? analyticOrbit,
                int segment,
                DateTimeOffset createdUtc)
            {
                Run = run;
                Sensors = sensors;
                DynamicsBackendVersion = dynamicsBackendVersion;
                AnalyticOrbit = analyticOrbit;
                Segment = segment;
                CreatedUtc = createdUtc;
            }

            public SimulationConfiguration Run { get; }
            public SensorDefinition[] Sensors { get; }
            public string DynamicsBackendVersion { get; }
            public AnalyticOrbit? AnalyticOrbit { get; }
            public int Segment { get; }
            public DateTimeOffset CreatedUtc { get; }
        }

        private sealed class StateItem : Item
        {
            public StateItem(SimulationState state)
            {
                State = state;
            }

            public SimulationState State { get; }
        }

        private sealed class OutputItem : Item
        {
            public OutputItem(SensorOutputSet output)
            {
                Output = output;
            }

            public SensorOutputSet Output { get; }
        }

        private sealed class TerminationItem : Item
        {
            public TerminationItem(Observation observation)
            {
                Observation = observation;
            }

            public Observation Observation { get; }
        }

        private sealed class DropItem : Item
        {
            public long States;
            public long Frames;
        }

        // One run directory, written only by the writer thread.
        private sealed class Segment
        {
            private readonly string _directory;
            private readonly BeginItem _begin;
            private readonly StreamWriter _states;
            private readonly StreamWriter _sensors;
            private string _dynamicsBackend;
            private ReferenceFrame? _earthFixedFrame;
            private long _stateLines;
            private long _sensorLines;

            public Segment(string directory, BeginItem begin)
            {
                if (Directory.Exists(directory))
                {
                    throw new IOException($"The run directory {directory} already exists; the recorder never overwrites a dataset.");
                }

                Directory.CreateDirectory(directory);
                _directory = directory;
                _begin = begin;
                _states = OpenLines(StatesFile);
                _sensors = OpenLines(SensorsFile);
            }

            public long DroppedStates { get; set; }
            public long DroppedFrames { get; set; }
            public Observation Termination { get; set; }

            public void Write(RunRecorder recorder, SimulationState state)
            {
                if (_dynamicsBackend == null)
                {
                    _dynamicsBackend = state.DynamicsBackend;
                    _earthFixedFrame = state.Spacecraft.EarthFixedFrame;
                    WriteManifest(recorder, complete: false);
                }

                WriteState(state);
            }

            public void Write(SensorOutputSet output)
            {
                foreach (ISensorFrame frame in output.Frames)
                {
                    WriteFrame(frame, output.SpacecraftSequence);
                }
            }

            public void Finish(RunRecorder recorder)
            {
                _states.Dispose();
                _sensors.Dispose();
                WriteManifest(recorder, complete: true);
            }

            public void Abandon()
            {
                try
                {
                    _states.Dispose();
                    _sensors.Dispose();
                }
                catch (IOException)
                {
                    // Already failing; the manifest stays "complete": false.
                }
            }

            private StreamWriter OpenLines(string file) =>
                new StreamWriter(new FileStream(Path.Combine(_directory, file), FileMode.CreateNew, FileAccess.Write), Utf8)
                {
                    NewLine = "\n"
                };

            // producing_command is the state's AppliedCommands: command k-1, applied over
            // [t_k-1, t_k), and null at t_0.
            private void WriteState(SimulationState state)
            {
                SpacecraftState spacecraft = state.Spacecraft;
                ActuatorCommandSet? command = state.AppliedCommands;
                DatasetJsonWriter json = new DatasetJsonWriter().BeginObject()
                    .Key("run_id").Value(state.RunId)
                    .Key("sequence").Value(spacecraft.Sequence)
                    .Key("simulation_time_s").Value(spacecraft.SimulationTimeSeconds)
                    .Key("timestamp_utc").Utc(spacecraft.TimestampUtc)
                    .Key("position_ecef_m").Value(spacecraft.PositionEcefMeters)
                    .Key("velocity_ecef_m_s").Value(spacecraft.VelocityEcefMetersPerSecond)
                    .Key("body_to_ecef_xyzw").Value(spacecraft.BodyToEcef)
                    .Key("angular_velocity_body_rad_s").Value(spacecraft.AngularVelocityBodyRadiansPerSecond)
                    .Key("producing_command");
                if (command.HasValue)
                {
                    ActuatorCommandSet applied = command.Value;
                    json.BeginObject()
                        .Key("sequence").Value(applied.Sequence)
                        .Key("apply_at_simulation_time_s").Value(applied.ApplyAtSimulationTimeSeconds)
                        .Key("reaction_wheel_torque_body_n_m").Value(applied.ReactionWheelTorqueBodyNewtonMeters)
                        .Key("magnetorquer_dipole_body_a_m2").Value(applied.MagnetorquerDipoleBodyAmpereSquareMeters)
                        .Key("thruster_force_body_n").Value(applied.ThrusterForceBodyNewtons)
                        .EndObject();
                }
                else
                {
                    json.Null();
                }

                json.Key("environment");
                if (state.HasEnvironment)
                {
                    EnvironmentState environment = state.Environment.Value;
                    json.BeginObject()
                        .Key("sun_position_itrf93_m").Value(environment.SunPositionItrf93Meters)
                        .Key("j2000_to_itrf93_xyzw").Value(environment.J2000ToItrf93)
                        .Key("earth_angular_velocity_itrf93_rad_s").Value(environment.EarthAngularVelocityItrf93RadiansPerSecond)
                        .Key("spacecraft_shadow_factor").Value(environment.SpacecraftShadowFactor)
                        .EndObject();
                }
                else
                {
                    json.Null();
                }

                _states.WriteLine(json.EndObject().ToString());
                _stateLines++;
            }

            private void WriteFrame(ISensorFrame frame, long stateSequence)
            {
                DatasetJsonWriter json = new DatasetJsonWriter().BeginObject()
                    .Key("run_id").Value(frame.RunId)
                    .Key("sensor_id").Value(frame.SensorId)
                    .Key("frame_id").Value(frame.FrameId)
                    .Key("sequence").Value(frame.Sequence)
                    .Key("state_sequence").Value(stateSequence)
                    .Key("simulation_time_s").Value(frame.SimulationTimeSeconds)
                    .Key("timestamp_utc").Utc(frame.TimestampUtc)
                    .Key("status").Value(frame.Status.ToString())
                    .Key("source").Value(frame.Source)
                    .Key("payload_type").Value(frame.PayloadType.Name)
                    .Key("payload");
                if (frame.Status == SensorFrameStatus.Unavailable)
                {
                    json.Null();
                }
                else
                {
                    WritePayload(json, frame);
                }

                _sensors.WriteLine(json.EndObject().ToString());
                _sensorLines++;
            }

            private void WritePayload(DatasetJsonWriter json, ISensorFrame frame)
            {
                switch (frame.UntypedPayload)
                {
                    case ImuMeasurement imu:
                        json.BeginObject()
                            .Key("angular_velocity_sensor_rad_s").Value(imu.AngularVelocitySensorRadiansPerSecond)
                            .Key("specific_force_sensor_m_s2").Value(imu.SpecificForceSensorMetersPerSecondSquared)
                            .EndObject();
                        break;
                    case AngularRateMeasurement rate:
                        json.BeginObject()
                            .Key("angular_velocity_sensor_rad_s").Value(rate.AngularVelocitySensorRadiansPerSecond)
                            .EndObject();
                        break;
                    case MagnetometerMeasurement magnetometer:
                        json.BeginObject()
                            .Key("magnetic_field_sensor_t").Value(magnetometer.MagneticFieldSensorTesla)
                            .EndObject();
                        break;
                    case LightSensorMeasurement light:
                        json.BeginObject()
                            .Key("irradiance_w_m2").Value(light.IrradianceWattsPerSquareMeter)
                            .EndObject();
                        break;
                    case ImageFrame image:
                        json.BeginObject()
                            .Key("image").Value(WriteImage(frame, image))
                            .Key("camera_id").Value(image.CameraId)
                            .Key("intrinsics");
                        WriteIntrinsics(json, image.Intrinsics);
                        json.Key("camera_position_ecef_m").Value(image.CameraPositionEcefMeters)
                            .Key("camera_to_ecef_xyzw").Value(image.CameraToEcef)
                            .Key("renderer").Value(image.Renderer)
                            .Key("exposure_start_simulation_time_s").Null()
                            .Key("exposure_duration_s").Null()
                            .EndObject();
                        break;
                    default:
                        throw new NotSupportedException(
                            $"Dataset v1 has no payload mapping for {frame.PayloadType.Name} (sensor {frame.SensorId}).");
                }
            }

            // Returns the PNG's path relative to the run directory, or null when the frame has no pixels.
            private string WriteImage(ISensorFrame frame, ImageFrame image)
            {
                if (image.Data == null || image.Data.Length == 0)
                {
                    return null;
                }

                if (!IsPathSafeId(frame.SensorId))
                {
                    throw new InvalidDataException($"Sensor ID '{frame.SensorId}' is not path-safe.");
                }

                CameraIntrinsics intrinsics = image.Intrinsics;
                byte[] png;
                switch (image.Encoding)
                {
                    case ImageEncoding.Rgb8:
                        png = PngEncoder.Encode(intrinsics.WidthPixels, intrinsics.HeightPixels, 3, image.Data);
                        break;
                    case ImageEncoding.Rgba8:
                        png = PngEncoder.Encode(intrinsics.WidthPixels, intrinsics.HeightPixels, 4, image.Data);
                        break;
                    case ImageEncoding.Png:
                        png = image.Data;
                        break;
                    default:
                        throw new NotSupportedException(
                            $"Cannot record {image.Encoding} frames from {frame.SensorId}: dataset v1 stores PNGs and Core has no JPEG decoder.");
                }

                string relative = "images/" + frame.SensorId + "/" + frame.Sequence.ToString("D8", CultureInfo.InvariantCulture) + ".png";
                string path = Path.Combine(_directory, "images", frame.SensorId);
                Directory.CreateDirectory(path);
                using (FileStream file = new FileStream(Path.Combine(_directory, relative), FileMode.CreateNew, FileAccess.Write))
                {
                    file.Write(png, 0, png.Length);
                }

                return relative;
            }

            private void WriteManifest(RunRecorder recorder, bool complete)
            {
                SimulationConfiguration run = _begin.Run;
                DatasetJsonWriter json = new DatasetJsonWriter().BeginObject()
                    .Key("schema_version").Value(SchemaVersion)
                    .Key("run_id").Value(run.RunId)
                    .Key("segment").Value(_begin.Segment)
                    .Key("created_utc").Utc(_begin.CreatedUtc)
                    .Key("complete").Value(complete)
                    .Key("termination");
                WriteTermination(json, Termination);
                json
                    .Key("writer").BeginObject()
                        .Key("process").Value(recorder._process)
                        .Key("argus_simulation_commit").Value(recorder._commit)
                        .Key("recorder_version").Value(RecorderVersion)
                        .EndObject()
                    .Key("dynamics_backend").BeginObject()
                        .Key("name").Value(_dynamicsBackend)
                        .Key("version").Value(_begin.DynamicsBackendVersion)
                        .EndObject()
                    .Key("earth_fixed_frame").Value(_earthFixedFrame?.ToString())
                    .Key("configuration").BeginObject()
                        .Key("epoch_utc").Utc(run.EpochUtc)
                        .Key("fixed_step_s").Value(run.FixedStepSeconds)
                        .Key("random_seed").Value(run.RandomSeed)
                        .Key("kernel_set_id").Value(run.KernelSetId)
                        .Key("initial_orbit");
                WriteInitialOrbit(json, run.InitialOrbit);
                json.Key("spacecraft");
                WriteSpacecraft(json, run.Spacecraft);
                json.Key("analytic_orbit");
                WriteAnalyticOrbit(json, _begin.AnalyticOrbit);
                json.EndObject().Key("sensors").BeginArray();
                foreach (SensorDefinition sensor in _begin.Sensors)
                {
                    WriteSensor(json, sensor, run.Sensors);
                }

                // Camera models arrive with CORE-6 (#42) and CORE-8 (#44).
                json.EndArray().Key("cameras").BeginArray().EndArray().Key("files").BeginObject();
                if (complete)
                {
                    json.Key(StatesFile).BeginObject().Key("lines").Value(_stateLines).EndObject()
                        .Key(SensorsFile).BeginObject().Key("lines").Value(_sensorLines).EndObject();
                }

                json.EndObject()
                    .Key("overflow").BeginObject()
                        .Key("policy").Value(OverflowPolicy)
                        .Key("capacity").Value(recorder._capacity)
                        .Key("dropped_states").Value(DroppedStates)
                        .Key("dropped_frames").Value(DroppedFrames)
                        .EndObject()
                    .EndObject();
                // Written beside it and swapped in, so a crash never leaves a half-written manifest.
                string path = Path.Combine(_directory, ManifestFile);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, json + "\n", Utf8);
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }

            private static void WriteInitialOrbit(DatasetJsonWriter json, ClassicalOrbitElements? orbit)
            {
                if (!orbit.HasValue)
                {
                    json.Null();
                    return;
                }

                ClassicalOrbitElements elements = orbit.Value;
                json.BeginObject()
                    .Key("semi_major_axis_m").Value(elements.SemiMajorAxisMeters)
                    .Key("eccentricity").Value(elements.Eccentricity)
                    .Key("inclination_deg").Value(elements.InclinationDegrees)
                    .Key("raan_deg").Value(elements.RaanDegrees)
                    .Key("argument_of_periapsis_deg").Value(elements.ArgumentOfPeriapsisDegrees)
                    .Key("true_anomaly_deg").Value(elements.TrueAnomalyDegrees)
                    .EndObject();
            }

            private static void WriteSpacecraft(DatasetJsonWriter json, SpacecraftConfiguration? spacecraft)
            {
                if (!spacecraft.HasValue)
                {
                    json.Null();
                    return;
                }

                SpacecraftConfiguration body = spacecraft.Value;
                json.BeginObject()
                    .Key("mass_kg").Value(body.MassKilograms)
                    .Key("inertia_body_kg_m2").Value(body.InertiaBodyKilogramSquareMeters)
                    .Key("initial_body_to_j2000_xyzw").Value(body.InitialBodyToJ2000)
                    .Key("initial_angular_velocity_body_rad_s").Value(body.InitialAngularVelocityBodyRadiansPerSecond)
                    .EndObject();
            }

            // The gateway's terminal observation: the last step that exists and why the run ended.
            private static void WriteTermination(DatasetJsonWriter json, Observation terminal)
            {
                if (terminal == null)
                {
                    json.Null();
                    return;
                }

                json.BeginObject()
                    .Key("status").Value(terminal.Status.ToString())
                    .Key("last_sequence").Value(terminal.Sequence)
                    .Key("last_simulation_time_s").Value(terminal.SimulationTimeSeconds)
                    .Key("last_timestamp_utc").Utc(terminal.TimestampUtc)
                    .Key("reason").Value(terminal.TerminationReason)
                    .EndObject();
            }

            private static void WriteAnalyticOrbit(DatasetJsonWriter json, AnalyticOrbit? orbit)
            {
                if (!orbit.HasValue)
                {
                    json.Null();
                    return;
                }

                json.BeginObject()
                    .Key("altitude_m").Value(orbit.Value.AltitudeMeters)
                    .Key("inclination_deg").Value(orbit.Value.InclinationDegrees)
                    .Key("raan_deg").Value(orbit.Value.RaanDegrees)
                    .Key("phase_deg").Value(orbit.Value.PhaseDegrees)
                    .EndObject();
            }

            private static void WriteIntrinsics(DatasetJsonWriter json, CameraIntrinsics intrinsics) =>
                json.BeginObject()
                    .Key("width_px").Value(intrinsics.WidthPixels)
                    .Key("height_px").Value(intrinsics.HeightPixels)
                    .Key("fx_px").Value(intrinsics.FocalLengthXPixels)
                    .Key("fy_px").Value(intrinsics.FocalLengthYPixels)
                    .Key("cx_px").Value(intrinsics.PrincipalPointXPixels)
                    .Key("cy_px").Value(intrinsics.PrincipalPointYPixels)
                    .EndObject();

            private static void WriteSensor(
                DatasetJsonWriter json,
                SensorDefinition sensor,
                IReadOnlyList<SensorConfiguration> configurations)
            {
                SensorConfiguration? configuration = null;
                foreach (SensorConfiguration candidate in configurations)
                {
                    if (candidate.Definition.SensorId == sensor.SensorId)
                    {
                        configuration = candidate;
                    }
                }

                // The run configuration's definition, where there is one, since the gateway decides
                // controller visibility from it; otherwise the model's own (an ideal sensor).
                SensorDefinition definition = configuration.HasValue ? configuration.Value.Definition : sensor;
                json.BeginObject()
                    .Key("sensor_id").Value(definition.SensorId)
                    .Key("model_name").Value(definition.ModelName)
                    .Key("frame_id").Value(definition.FrameId)
                    .Key("sample_period_s").Value(definition.SamplePeriodSeconds)
                    .Key("source").Value(definition.Source)
                    .Key("mount").BeginObject()
                        .Key("position_body_m").Value(definition.Mount.PositionBodyMeters)
                        .Key("sensor_to_body_xyzw").Value(definition.Mount.SensorToBody)
                        .EndObject()
                    .Key("controller_visible").Value(definition.IsControllerVisible);

                if (!configuration.HasValue)
                {
                    json.Key("kind").Null().Key("profile").Null().EndObject();
                    return;
                }

                SensorConfiguration found = configuration.Value;
                json.Key("kind").Value(found.Kind.ToString()).Key("profile");
                switch (found.Kind)
                {
                    case SensorKind.Imu:
                        GyroscopeProfile gyroscope = found.ImuProfile.Gyroscope;
                        AccelerometerProfile accelerometer = found.ImuProfile.Accelerometer;
                        json.BeginObject()
                            .Key("gyroscope").BeginObject()
                                .Key("noise_density_rad_s_per_rt_hz").Value(gyroscope.NoiseDensityRadiansPerSecondPerRootHertz)
                                .Key("bias_random_walk_rad_s_per_rt_s").Value(gyroscope.BiasRandomWalkRadiansPerSecondPerRootSecond)
                                .Key("bias_random_walk_bound_rad_s").Value(gyroscope.BiasRandomWalkBoundRadiansPerSecond)
                                .Key("constant_bias_sensor_rad_s").Value(gyroscope.ConstantBiasSensorRadiansPerSecond)
                                .Key("scale_factor_per_axis").Value(gyroscope.ScaleFactorPerAxis)
                                .Key("range_rad_s").Value(gyroscope.RangeRadiansPerSecond)
                                .Key("resolution_rad_s").Value(gyroscope.ResolutionRadiansPerSecond)
                                .EndObject()
                            .Key("accelerometer").BeginObject()
                                .Key("noise_density_m_s2_per_rt_hz").Value(accelerometer.NoiseDensityMetersPerSecondSquaredPerRootHertz)
                                .Key("bias_random_walk_m_s2_per_rt_s").Value(accelerometer.BiasRandomWalkMetersPerSecondSquaredPerRootSecond)
                                .Key("bias_random_walk_bound_m_s2").Value(accelerometer.BiasRandomWalkBoundMetersPerSecondSquared)
                                .Key("constant_bias_sensor_m_s2").Value(accelerometer.ConstantBiasSensorMetersPerSecondSquared)
                                .Key("scale_factor_per_axis").Value(accelerometer.ScaleFactorPerAxis)
                                .Key("range_m_s2").Value(accelerometer.RangeMetersPerSecondSquared)
                                .Key("resolution_m_s2").Value(accelerometer.ResolutionMetersPerSecondSquared)
                                .EndObject()
                            .EndObject();
                        break;
                    case SensorKind.Magnetometer:
                        MagnetometerProfile magnetometer = found.MagnetometerProfile;
                        json.BeginObject()
                            .Key("noise_std_t").Value(magnetometer.NoiseStdTesla)
                            .Key("constant_bias_sensor_t").Value(magnetometer.ConstantBiasSensorTesla)
                            .Key("scale_factor").Value(magnetometer.ScaleFactor)
                            .Key("range_t").Value(magnetometer.RangeTesla)
                            .EndObject();
                        break;
                    case SensorKind.LightSensor:
                        LightSensorProfile light = found.LightSensorProfile;
                        json.BeginObject()
                            .Key("field_of_view_half_angle_rad").Value(light.FieldOfViewHalfAngleRadians)
                            .Key("scale_factor").Value(light.ScaleFactor)
                            .Key("noise_std_w_m2").Value(light.NoiseStdWattsPerSquareMeter)
                            .Key("bias_w_m2").Value(light.BiasWattsPerSquareMeter)
                            .Key("saturation_w_m2").Value(light.SaturationWattsPerSquareMeter)
                            .EndObject();
                        break;
                    default:
                        json.Null();
                        break;
                }

                json.EndObject();
            }
        }
    }
}
