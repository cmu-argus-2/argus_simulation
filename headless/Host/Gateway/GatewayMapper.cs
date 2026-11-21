using System;
using System.Collections.Generic;
using Argus.Simulation.Core;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using PbGateway = Argus.Contracts.Gateway.V1;
using PbSim = Argus.Contracts.Sim.V1;

namespace Argus.Simulation.Host
{
    // Maps between Core and argus/gateway/v1 (CON-3, Argus.Contracts/README.md "Controller gateway").
    // Observations go out with their status and controller-visible frames only, never a termination
    // reason or truth (D7); commands come in as CommandForStep, and one that doesn't decode into a
    // DeviceCommandSet is reported by field instead of reaching the gateway.
    internal static class GatewayMapper
    {
        // gRPC's default message limit, which a session never goes below.
        public const long DefaultMaxMessageBytes = 4L * 1024 * 1024;

        // Room in an observation for everything but camera pixels.
        private const long ObservationOverheadBytes = 1024 * 1024;

        // SessionStarted.max_message_bytes for a run: the sum of every controller-visible camera's
        // RGB8 image, since one observation can carry a frame from each, plus room for the rest.
        public static long MaxMessageBytes(SimulationConfiguration configuration)
        {
            long pixels = 0;
            foreach (DeviceConfiguration device in configuration.Devices)
            {
                if (device.Definition.IsControllerVisible && device.KindDefinition is ICameraKind camera)
                {
                    CameraIntrinsics intrinsics = camera.Profile(device).Intrinsics;
                    pixels += (long)intrinsics.WidthPixels * intrinsics.HeightPixels * 3;
                }
            }

            return pixels == 0 ? DefaultMaxMessageBytes : Math.Max(DefaultMaxMessageBytes, pixels + ObservationOverheadBytes);
        }

        // The kind of every configured device, by device ID, for naming each frame's kind.
        public static IReadOnlyDictionary<string, IDeviceKind> Kinds(SimulationConfiguration configuration)
        {
            Dictionary<string, IDeviceKind> kinds = new Dictionary<string, IDeviceKind>(StringComparer.Ordinal);
            foreach (DeviceConfiguration device in configuration.Devices)
            {
                kinds[device.Definition.SensorId] = device.KindDefinition;
            }

            return kinds;
        }

        public static PbGateway.Observation ToProto(Observation observation, IReadOnlyDictionary<string, IDeviceKind> kinds)
        {
            PbGateway.Observation message = new PbGateway.Observation
            {
                RunId = observation.RunId,
                Sequence = observation.Sequence,
                SimulationTimeS = observation.SimulationTimeSeconds,
                TimestampUtc = Timestamp.FromDateTimeOffset(observation.TimestampUtc),
                Source = observation.Source,
                Status = ToProto(observation.Status)
            };
            foreach (ISensorFrame frame in observation.Frames)
            {
                message.Frames.Add(ToProto(frame, kinds));
            }

            return message;
        }

        // A camera's frame carries its CameraObservation and no payload map; any other kind's carries
        // its output map, only when Valid or Stale.
        private static PbGateway.ObservationFrame ToProto(ISensorFrame frame, IReadOnlyDictionary<string, IDeviceKind> kinds)
        {
            if (!kinds.TryGetValue(frame.SensorId, out IDeviceKind kind))
            {
                throw new InvalidOperationException($"Observation frame of sensor '{frame.SensorId}', which the run does not configure.");
            }

            PbGateway.ObservationFrame message = new PbGateway.ObservationFrame
            {
                Frame = SimProtoMapper.ToProtoEnvelope(frame, kind.Name)
            };
            if (frame.UntypedPayload is CameraObservation camera)
            {
                message.Camera = ToProto(camera);
                return message;
            }

            if (!(kind is IBackendSensorKind backend))
            {
                throw new InvalidOperationException(
                    $"Sensor '{frame.SensorId}' of kind '{kind.Name}' has no wire form for a controller.");
            }

            if (frame.Status == SensorFrameStatus.Valid || frame.Status == SensorFrameStatus.Stale)
            {
                SimProtoMapper.AddAll(message.Frame.Payload, backend.WriteOutput(frame.UntypedPayload));
            }

            return message;
        }

        private static PbGateway.CameraObservation ToProto(CameraObservation camera)
        {
            PbGateway.CameraObservation message = new PbGateway.CameraObservation
            {
                CameraId = camera.CameraId,
                Sequence = camera.Sequence,
                ExposureStartSimulationTimeS = camera.ExposureStartSimulationTimeSeconds,
                ExposureStartUtc = Timestamp.FromDateTimeOffset(camera.ExposureStartUtc),
                ExposureDurationS = camera.ExposureDurationSeconds,
                WidthPx = camera.WidthPixels,
                HeightPx = camera.HeightPixels
            };
            if (!camera.Pixels.IsEmpty)
            {
                message.PixelsRgb8 = UnsafeByteOperations.UnsafeWrap(camera.Pixels);
            }

            return message;
        }

        // Core's statuses start at Ok = 0; the wire's 0 is UNSPECIFIED.
        private static PbGateway.ObservationStatus ToProto(ObservationStatus status) => status switch
        {
            ObservationStatus.Ok => PbGateway.ObservationStatus.Ok,
            ObservationStatus.BackendFailed => PbGateway.ObservationStatus.BackendFailed,
            ObservationStatus.Stopped => PbGateway.ObservationStatus.Stopped,
            _ => throw new ArgumentException($"Observation status {status} has no wire form.", nameof(status))
        };

        public static PbGateway.StepDriver ToProto(StepDriver driver) => driver switch
        {
            StepDriver.Host => PbGateway.StepDriver.Host,
            StepDriver.Controller => PbGateway.StepDriver.Controller,
            _ => throw new ArgumentException($"Step driver {driver} has no wire form.", nameof(driver))
        };

        // The Core command a CommandForStep decodes into, or null with the first field that keeps it
        // from decoding. A command that decodes may still be refused by the gateway, which logs it.
        public static DeviceCommandSet? TryDecode(PbGateway.CommandForStep command, string path, out string problem)
        {
            if (command == null)
            {
                problem = $"{path}: required field is not set";
                return null;
            }

            if (!command.HasSequence)
            {
                problem = $"{path}.sequence: required field is not set";
                return null;
            }

            if (!command.HasApplyAtSimulationTimeS)
            {
                problem = $"{path}.apply_at_simulation_time_s: required field is not set";
                return null;
            }

            if (command.Commands == null)
            {
                problem = $"{path}.commands: required field is not set";
                return null;
            }

            DeviceCommand[] devices = new DeviceCommand[command.Commands.Commands.Count];
            for (int index = 0; index < devices.Length; index++)
            {
                PbSim.DeviceCommand device = command.Commands.Commands[index];
                string devicePath = $"{path}.commands.commands[{index}]";
                DeviceValues values = TryDecode(device.Values, devicePath + ".values", out problem);
                if (values == null)
                {
                    return null;
                }

                devices[index] = new DeviceCommand(device.DeviceId, values);
            }

            problem = null;
            return new DeviceCommandSet(command.Sequence, command.ApplyAtSimulationTimeS, devices);
        }

        private static DeviceValues TryDecode(MapField<string, PbSim.DeviceValue> map, string path, out string problem)
        {
            DeviceValues.Builder values = new DeviceValues.Builder();
            foreach (KeyValuePair<string, PbSim.DeviceValue> entry in map)
            {
                DeviceValue? value = TryDecode(entry.Value);
                if (value == null)
                {
                    problem = $"{path}[\"{entry.Key}\"]: no value is set";
                    return null;
                }

                values.Add(entry.Key, value.Value);
            }

            problem = null;
            return values.Build();
        }

        private static DeviceValue? TryDecode(PbSim.DeviceValue value)
        {
            switch (value?.ValueCase ?? PbSim.DeviceValue.ValueOneofCase.None)
            {
                case PbSim.DeviceValue.ValueOneofCase.Number:
                    return DeviceValue.FromNumber(value.Number);
                case PbSim.DeviceValue.ValueOneofCase.Vector3 when value.Vector3 != null:
                    return DeviceValue.FromVector3(new Vector3d(value.Vector3.X, value.Vector3.Y, value.Vector3.Z));
                case PbSim.DeviceValue.ValueOneofCase.Quaternion when value.Quaternion != null:
                    PbSim.Quaternion q = value.Quaternion;
                    return DeviceValue.FromQuaternion(new Quaterniond(q.X, q.Y, q.Z, q.W));
                case PbSim.DeviceValue.ValueOneofCase.Matrix3
                    when value.Matrix3?.Row0 != null && value.Matrix3.Row1 != null && value.Matrix3.Row2 != null:
                    PbSim.Matrix3 m = value.Matrix3;
                    return DeviceValue.FromMatrix3(new Matrix3d(
                        new Vector3d(m.Row0.X, m.Row0.Y, m.Row0.Z),
                        new Vector3d(m.Row1.X, m.Row1.Y, m.Row1.Z),
                        new Vector3d(m.Row2.X, m.Row2.Y, m.Row2.Z)));
                case PbSim.DeviceValue.ValueOneofCase.Flag:
                    return DeviceValue.FromFlag(value.Flag);
                case PbSim.DeviceValue.ValueOneofCase.Text:
                    return DeviceValue.FromText(value.Text);
                default:
                    return null;
            }
        }
    }
}
