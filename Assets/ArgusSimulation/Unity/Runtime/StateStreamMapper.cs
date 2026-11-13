using System;
using System.Collections.Generic;
using Argus.Simulation.Core;
using Google.Protobuf.Collections;
using PbSim = Argus.Contracts.Sim.V1;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Unity
{
    // One received state-stream pair in Core types (CON-1): its run generation, the host's state and
    // its latest-per-sensor projection.
    public readonly struct StreamPair
    {
        public StreamPair(string runGeneration, SimulationState state, SensorOutputSet output)
        {
            RunGeneration = runGeneration;
            State = state;
            Output = output;
        }

        public string RunGeneration { get; }
        public SimulationState State { get; }
        public SensorOutputSet Output { get; }
    }

    // Maps argus.stream.v1 to Core (Argus.Contracts/README.md "State stream"). Core never sees
    // Protobuf, and no Basilisk type exists here (D8). Payloads decode through the registered Core
    // kind each frame names (D11). Throws FormatException naming the field for a response that breaks
    // the contract, so a bad response is never published.
    public static class StateStreamMapper
    {
        public static StreamPair ToCore(PbStream.SubscribeResponse response)
        {
            if (string.IsNullOrEmpty(response.RunGeneration))
            {
                throw Invalid("run_generation", "must not be empty");
            }

            PbSim.SensorOutputSet output = response.SensorOutput ?? throw Unset("sensor_output");
            ISensorFrame[] frames = new ISensorFrame[output.Frames.Count];
            Dictionary<string, IBackendSensorKind> kinds = new Dictionary<string, IBackendSensorKind>(StringComparer.Ordinal);
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i] = ToCore(output.Frames[i], $"sensor_output.frames[{i}]", kinds);
            }

            SimulationState state = ToCore(response.State ?? throw Unset("state"), kinds);
            if (!output.HasSpacecraftSequence || output.SpacecraftSequence != state.Spacecraft.Sequence)
            {
                throw Invalid("sensor_output.spacecraft_sequence", "must be the state's sequence");
            }

            if (!output.HasSimulationTimeS || output.RunId != state.RunId)
            {
                throw Invalid("sensor_output", "must carry the state's run ID and a simulation time");
            }

            RequireStepAndTime("sensor_output", output.SpacecraftSequence, output.SimulationTimeS);

            SensorOutputSet set = new SensorOutputSet(output.RunId, output.SpacecraftSequence, output.SimulationTimeS, frames);
            if (!set.IsValid)
            {
                throw Invalid("sensor_output", "has an invalid frame, or one captured after the state");
            }

            return new StreamPair(response.RunGeneration, state, set);
        }

        private static SimulationState ToCore(PbSim.SimulationState message, IReadOnlyDictionary<string, IBackendSensorKind> kinds)
        {
            PbSim.SpacecraftState spacecraft = message.Spacecraft ?? throw Unset("state.spacecraft");
            if (!spacecraft.HasSequence || !spacecraft.HasSimulationTimeS)
            {
                throw Unset("state.spacecraft.sequence and simulation_time_s");
            }

            RequireStepAndTime("state.spacecraft", spacecraft.Sequence, spacecraft.SimulationTimeS);

            SpacecraftState spacecraftState = new SpacecraftState(
                spacecraft.Sequence,
                spacecraft.SimulationTimeS,
                (spacecraft.TimestampUtc ?? throw Unset("state.spacecraft.timestamp_utc")).ToDateTimeOffset(),
                ToCore(spacecraft.PositionEarthFixedM, "state.spacecraft.position_earth_fixed_m"),
                ToCore(spacecraft.VelocityEarthFixedMPerS, "state.spacecraft.velocity_earth_fixed_m_per_s"),
                ToUnit(spacecraft.BodyToEarthFixed, "state.spacecraft.body_to_earth_fixed"),
                ToCore(spacecraft.AngularVelocityBodyRadPerS, "state.spacecraft.angular_velocity_body_rad_per_s"),
                ToCore(spacecraft.EarthFixedFrame));

            DeviceCommandSet? applied = null;
            if (message.AppliedCommands != null)
            {
                PbSim.AppliedCommand command = message.AppliedCommands;
                if (!command.HasSequence || !command.HasApplyAtSimulationTimeS)
                {
                    throw Unset("state.applied_commands.sequence and apply_at_simulation_time_s");
                }

                RequireStepAndTime("state.applied_commands", command.Sequence, command.ApplyAtSimulationTimeS);

                DeviceCommand[] commands = new DeviceCommand[command.Commands.Count];
                for (int i = 0; i < commands.Length; i++)
                {
                    PbSim.DeviceCommand device = command.Commands[i];
                    commands[i] = new DeviceCommand(
                        device.DeviceId, ToCore(device.Values, $"state.applied_commands.commands[{i}].values"));
                }

                applied = new DeviceCommandSet(command.Sequence, command.ApplyAtSimulationTimeS, commands);
            }

            EnvironmentState? environment = null;
            if (message.Environment != null)
            {
                PbSim.EnvironmentState env = message.Environment;
                if (!env.HasSpacecraftShadowFactor)
                {
                    throw Unset("state.environment.spacecraft_shadow_factor");
                }

                // 0 is umbra and 1 full illumination.
                if (!(env.SpacecraftShadowFactor >= 0.0 && env.SpacecraftShadowFactor <= 1.0))
                {
                    throw Invalid("state.environment.spacecraft_shadow_factor", $"must be within [0, 1], got {env.SpacecraftShadowFactor}");
                }

                environment = new EnvironmentState(
                    ToCore(env.SunPositionItrf93M, "state.environment.sun_position_itrf93_m"),
                    ToUnit(env.J2000ToItrf93, "state.environment.j2000_to_itrf93"),
                    ToCore(env.EarthAngularVelocityItrf93RadPerS, "state.environment.earth_angular_velocity_itrf93_rad_per_s"),
                    env.SpacecraftShadowFactor);
            }

            // The snapshot step's backend samples, decoded by the kind its frame names: a sensor
            // sampled at this step also published a frame at it.
            Dictionary<string, object> measurements = new Dictionary<string, object>(StringComparer.Ordinal);
            List<string> unavailable = new List<string>();
            for (int i = 0; i < message.BackendSamples.Count; i++)
            {
                PbSim.BackendSensorSample sample = message.BackendSamples[i];
                string path = $"state.backend_samples[{i}]";
                if (!kinds.TryGetValue(sample.SensorId, out IBackendSensorKind kind))
                {
                    throw Invalid(path, $"sensor '{sample.SensorId}' has no frame naming its kind");
                }

                if (measurements.ContainsKey(sample.SensorId) || unavailable.Contains(sample.SensorId))
                {
                    throw Invalid(path, $"repeats sensor '{sample.SensorId}'");
                }

                if (sample.Output.Count == 0)
                {
                    unavailable.Add(sample.SensorId);
                }
                else
                {
                    measurements.Add(sample.SensorId, Read(kind, sample.Output, $"{path}.output"));
                }
            }

            SimulationState state = new SimulationState(
                message.RunId,
                message.DynamicsBackend,
                spacecraftState,
                applied,
                environment,
                new SensorMeasurementSet(measurements, unavailable));
            if (!state.IsValid)
            {
                throw Invalid("state", "is not a valid SimulationState (see SimulationState.IsValid)");
            }

            return state;
        }

        private static ISensorFrame ToCore(PbSim.SensorFrame frame, string path, Dictionary<string, IBackendSensorKind> kinds)
        {
            IBackendSensorKind kind = DeviceKinds.Find(frame.Kind) as IBackendSensorKind
                ?? throw Invalid($"{path}.kind", $"'{frame.Kind}' is not a registered kind whose frames the stream carries");
            if (!frame.HasSequence || !frame.HasSimulationTimeS || !frame.HasCaptureStateSequence)
            {
                throw Unset($"{path}.sequence, simulation_time_s and capture_state_sequence");
            }

            RequireStepAndTime(path, frame.Sequence, frame.SimulationTimeS);
            if (frame.CaptureStateSequence < 0)
            {
                throw Invalid($"{path}.capture_state_sequence", $"must be >= 0, got {frame.CaptureStateSequence}");
            }

            if (kinds.ContainsKey(frame.SensorId))
            {
                throw Invalid(path, $"repeats sensor '{frame.SensorId}'");
            }

            kinds.Add(frame.SensorId, kind);
            try
            {
                return kind.CreateFrame(
                    frame.RunId,
                    frame.SensorId,
                    frame.FrameId,
                    frame.Sequence,
                    frame.CaptureStateSequence,
                    frame.SimulationTimeS,
                    (frame.TimestampUtc ?? throw Unset($"{path}.timestamp_utc")).ToDateTimeOffset(),
                    ToCore(frame.Status, $"{path}.status"),
                    frame.Source,
                    ToCore(frame.Payload, $"{path}.payload"));
            }
            catch (ArgumentException exception)
            {
                throw Invalid($"{path}.payload", exception.Message);
            }
        }

        private static object Read(IBackendSensorKind kind, MapField<string, PbSim.DeviceValue> output, string path)
        {
            try
            {
                return kind.ReadOutput(ToCore(output, path));
            }
            catch (ArgumentException exception)
            {
                throw Invalid(path, exception.Message);
            }
        }

        private static DeviceValues ToCore(MapField<string, PbSim.DeviceValue> map, string path)
        {
            DeviceValues.Builder values = new DeviceValues.Builder();
            foreach (KeyValuePair<string, PbSim.DeviceValue> entry in map)
            {
                values.Add(entry.Key, ToCore(entry.Value, $"{path}[\"{entry.Key}\"]"));
            }

            return values.Build();
        }

        private static DeviceValue ToCore(PbSim.DeviceValue value, string path) => value.ValueCase switch
        {
            PbSim.DeviceValue.ValueOneofCase.Number => DeviceValue.FromNumber(value.Number),
            PbSim.DeviceValue.ValueOneofCase.Vector3 => DeviceValue.FromVector3(ToCore(value.Vector3, path)),
            PbSim.DeviceValue.ValueOneofCase.Quaternion => DeviceValue.FromQuaternion(ToCore(value.Quaternion, path)),
            PbSim.DeviceValue.ValueOneofCase.Matrix3 => DeviceValue.FromMatrix3(new Matrix3d(
                ToCore(value.Matrix3.Row0, path + ".row0"),
                ToCore(value.Matrix3.Row1, path + ".row1"),
                ToCore(value.Matrix3.Row2, path + ".row2"))),
            PbSim.DeviceValue.ValueOneofCase.Flag => DeviceValue.FromFlag(value.Flag),
            PbSim.DeviceValue.ValueOneofCase.Text => DeviceValue.FromText(value.Text),
            _ => throw Unset(path)
        };

        // CON-1 ranges the Core types do not check themselves: a nonnegative sequence and a finite,
        // nonnegative simulation time.
        private static void RequireStepAndTime(string path, long sequence, double simulationTimeSeconds)
        {
            if (sequence < 0)
            {
                throw Invalid($"{path}.sequence", $"must be >= 0, got {sequence}");
            }

            if (!(simulationTimeSeconds >= 0.0) || double.IsInfinity(simulationTimeSeconds))
            {
                throw Invalid($"{path}.simulation_time_s", $"must be finite and >= 0, got {simulationTimeSeconds}");
            }
        }

        // An attitude: a unit quaternion, never (0, 0, 0, 0).
        private static Quaterniond ToUnit(PbSim.Quaternion value, string path)
        {
            Quaterniond quaternion = ToCore(value, path);
            return quaternion.IsUnit ? quaternion : throw Invalid(path, "must be a unit quaternion");
        }

        private static Vector3d ToCore(PbSim.Vector3 value, string path) =>
            value == null ? throw Unset(path) : new Vector3d(value.X, value.Y, value.Z);

        private static Quaterniond ToCore(PbSim.Quaternion value, string path) =>
            value == null ? throw Unset(path) : new Quaterniond(value.X, value.Y, value.Z, value.W);

        private static ReferenceFrame ToCore(PbSim.ReferenceFrame frame) => frame switch
        {
            PbSim.ReferenceFrame.AnalyticEarthFixed => ReferenceFrame.AnalyticEarthFixed,
            PbSim.ReferenceFrame.Itrf93 => ReferenceFrame.Itrf93,
            _ => throw Invalid("state.spacecraft.earth_fixed_frame", $"{frame} is not a frame")
        };

        // The wire's 0 is UNSPECIFIED, but Core's Valid is 0, so every status maps explicitly.
        private static SensorFrameStatus ToCore(PbSim.SensorFrameStatus status, string path) => status switch
        {
            PbSim.SensorFrameStatus.Valid => SensorFrameStatus.Valid,
            PbSim.SensorFrameStatus.Unavailable => SensorFrameStatus.Unavailable,
            PbSim.SensorFrameStatus.Stale => SensorFrameStatus.Stale,
            PbSim.SensorFrameStatus.Invalid => SensorFrameStatus.Invalid,
            _ => throw Invalid(path, $"{status} is not a status")
        };

        private static FormatException Unset(string path) => new FormatException($"{path}: required field is not set");

        private static FormatException Invalid(string path, string reason) => new FormatException($"{path}: {reason}");
    }
}
