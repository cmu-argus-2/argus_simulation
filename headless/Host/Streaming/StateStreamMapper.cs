using System;
using System.Collections.Generic;
using Argus.Simulation.Core;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using PbSim = Argus.Contracts.Sim.V1;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Host
{
    // Maps one stream pair to argus.stream.v1 (CON-1, Argus.Contracts/README.md "Presence and
    // encoding"). It carries only Core contracts, never Basilisk types (D8). Payloads are each
    // kind's output map under its declared keys (IBackendSensorKind.WriteOutput, D11). A state, frame
    // or value that is not valid and finite throws rather than reaching a client.
    internal static class StateStreamMapper
    {
        public static PbStream.SubscribeResponse ToProto(StreamSnapshot snapshot)
        {
            SimulationState state = snapshot.State;
            if (!state.IsValid)
            {
                throw new InvalidOperationException($"State {state.Spacecraft.Sequence} is not a valid, finite SimulationState.");
            }

            PbSim.SensorOutputSet output = new PbSim.SensorOutputSet
            {
                RunId = state.RunId,
                SpacecraftSequence = state.Spacecraft.Sequence,
                SimulationTimeS = state.Spacecraft.SimulationTimeSeconds
            };
            foreach (ISensorFrame frame in snapshot.Frames)
            {
                output.Frames.Add(ToProto(frame, snapshot.Generation.Streamed[frame.SensorId]));
            }

            return new PbStream.SubscribeResponse
            {
                State = ToProto(state, snapshot.Generation),
                SensorOutput = output,
                RunGeneration = snapshot.Generation.Id
            };
        }

        private static PbSim.SimulationState ToProto(SimulationState state, StreamGeneration generation)
        {
            SpacecraftState spacecraft = state.Spacecraft;
            PbSim.SimulationState message = new PbSim.SimulationState
            {
                RunId = state.RunId,
                DynamicsBackend = state.DynamicsBackend,
                Spacecraft = new PbSim.SpacecraftState
                {
                    Sequence = spacecraft.Sequence,
                    SimulationTimeS = spacecraft.SimulationTimeSeconds,
                    TimestampUtc = Timestamp.FromDateTimeOffset(spacecraft.TimestampUtc),
                    EarthFixedFrame = ToProto(spacecraft.EarthFixedFrame),
                    PositionEarthFixedM = SimProtoMapper.ToProto(spacecraft.PositionEcefMeters),
                    VelocityEarthFixedMPerS = SimProtoMapper.ToProto(spacecraft.VelocityEcefMetersPerSecond),
                    BodyToEarthFixed = SimProtoMapper.ToProto(spacecraft.BodyToEcef),
                    AngularVelocityBodyRadPerS = SimProtoMapper.ToProto(spacecraft.AngularVelocityBodyRadiansPerSecond)
                }
            };

            // Absent at t_0 and for a source that applies no commands.
            if (state.HasAppliedCommands)
            {
                DeviceCommandSet applied = state.AppliedCommands.Value;
                PbSim.AppliedCommand command = new PbSim.AppliedCommand
                {
                    Sequence = applied.Sequence,
                    ApplyAtSimulationTimeS = applied.ApplyAtSimulationTimeSeconds
                };
                foreach (DeviceCommand device in applied.Commands)
                {
                    PbSim.DeviceCommand values = new PbSim.DeviceCommand { DeviceId = device.DeviceId };
                    AddFinite(values.Values, device.Values, $"state.applied_commands device '{device.DeviceId}'");
                    command.Commands.Add(values);
                }

                message.AppliedCommands = command;
            }

            // Present exactly for ITRF93 states (D4).
            if (state.HasEnvironment)
            {
                EnvironmentState environment = state.Environment.Value;
                message.Environment = new PbSim.EnvironmentState
                {
                    SunPositionItrf93M = SimProtoMapper.ToProto(environment.SunPositionItrf93Meters),
                    J2000ToItrf93 = SimProtoMapper.ToProto(environment.J2000ToItrf93),
                    EarthAngularVelocityItrf93RadPerS = SimProtoMapper.ToProto(environment.EarthAngularVelocityItrf93RadiansPerSecond),
                    SpacecraftShadowFactor = environment.SpacecraftShadowFactor
                };
            }

            // The snapshot step's backend samples: an empty map is sampled without data, and a sensor
            // with no entry was not sampled this step.
            foreach (DeviceConfiguration device in generation.Run.BackendSensors)
            {
                string sensorId = device.Definition.SensorId;
                if (!state.SensorMeasurements.WasSampled(sensorId))
                {
                    continue;
                }

                PbSim.BackendSensorSample sample = new PbSim.BackendSensorSample { SensorId = sensorId };
                if (state.SensorMeasurements.TryGetUntypedMeasurement(sensorId, out object measurement))
                {
                    AddFinite(
                        sample.Output,
                        ((IBackendSensorKind)device.KindDefinition).WriteOutput(measurement),
                        $"state.backend_samples '{sensorId}'");
                }

                message.BackendSamples.Add(sample);
            }

            return message;
        }

        // A cached frame keeps its envelope unchanged; only Valid and Stale frames carry a payload.
        private static PbSim.SensorFrame ToProto(ISensorFrame frame, IBackendSensorKind kind)
        {
            if (!frame.HasValidEnvelope)
            {
                throw new InvalidOperationException($"Frame {frame.Sequence} of sensor '{frame.SensorId}' has an invalid envelope.");
            }

            PbSim.SensorFrame message = new PbSim.SensorFrame
            {
                RunId = frame.RunId,
                SensorId = frame.SensorId,
                FrameId = frame.FrameId,
                Sequence = frame.Sequence,
                SimulationTimeS = frame.SimulationTimeSeconds,
                TimestampUtc = Timestamp.FromDateTimeOffset(frame.TimestampUtc),
                Status = ToProto(frame.Status),
                Source = frame.Source,
                CaptureStateSequence = frame.StateSequence,
                Kind = kind.Name
            };
            if (frame.Status == SensorFrameStatus.Valid || frame.Status == SensorFrameStatus.Stale)
            {
                AddFinite(message.Payload, kind.WriteOutput(frame.UntypedPayload), $"sensor_output frame '{frame.SensorId}'");
            }

            return message;
        }

        private static void AddFinite(MapField<string, PbSim.DeviceValue> map, DeviceValues values, string path)
        {
            foreach (KeyValuePair<string, DeviceValue> entry in values)
            {
                if (!entry.Value.IsFinite)
                {
                    throw new InvalidOperationException($"{path} value \"{entry.Key}\" is not finite.");
                }
            }

            SimProtoMapper.AddAll(map, values);
        }

        private static PbSim.ReferenceFrame ToProto(ReferenceFrame frame) => frame switch
        {
            ReferenceFrame.AnalyticEarthFixed => PbSim.ReferenceFrame.AnalyticEarthFixed,
            ReferenceFrame.Itrf93 => PbSim.ReferenceFrame.Itrf93,
            _ => throw new ArgumentException($"Reference frame {frame} has no wire form.", nameof(frame))
        };

        // Core Valid is 0, but the wire's 0 is UNSPECIFIED, so every status maps explicitly.
        private static PbSim.SensorFrameStatus ToProto(SensorFrameStatus status) => status switch
        {
            SensorFrameStatus.Valid => PbSim.SensorFrameStatus.Valid,
            SensorFrameStatus.Unavailable => PbSim.SensorFrameStatus.Unavailable,
            SensorFrameStatus.Stale => PbSim.SensorFrameStatus.Stale,
            SensorFrameStatus.Invalid => PbSim.SensorFrameStatus.Invalid,
            _ => throw new ArgumentException($"Sensor frame status {status} has no wire form.", nameof(status))
        };
    }
}
