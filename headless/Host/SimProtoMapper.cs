using System;
using System.Collections.Generic;
using Argus.Simulation.Core;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using PbSim = Argus.Contracts.Sim.V1;

namespace Argus.Simulation.Host
{
    // Core to argus.sim.v1 for the shared types the links carry: the Basilisk link
    // (BasiliskProtoMapper), the state stream (StateStreamMapper) and the controller gateway
    // (GatewayMapper). Nothing here is Basilisk's (D8).
    internal static class SimProtoMapper
    {
        public static void AddAll(MapField<string, PbSim.DeviceValue> map, DeviceValues values)
        {
            foreach (KeyValuePair<string, DeviceValue> entry in values)
            {
                map.Add(entry.Key, ToProto(entry.Value));
            }
        }

        public static PbSim.DeviceValue ToProto(DeviceValue value) => value.Type switch
        {
            DeviceValueType.Number => new PbSim.DeviceValue { Number = value.Number },
            DeviceValueType.Vector3 => new PbSim.DeviceValue { Vector3 = ToProto(value.Vector3) },
            DeviceValueType.Quaternion => new PbSim.DeviceValue { Quaternion = ToProto(value.Quaternion) },
            DeviceValueType.Matrix3 => new PbSim.DeviceValue { Matrix3 = ToProto(value.Matrix3) },
            DeviceValueType.Flag => new PbSim.DeviceValue { Flag = value.Flag },
            DeviceValueType.Text => new PbSim.DeviceValue { Text = value.Text },
            _ => throw new ArgumentException($"Device value type {value.Type} has no wire form.", nameof(value))
        };

        public static PbSim.Vector3 ToProto(Vector3d value) =>
            new PbSim.Vector3 { X = value.X, Y = value.Y, Z = value.Z };

        public static PbSim.Quaternion ToProto(Quaterniond value) =>
            new PbSim.Quaternion { X = value.X, Y = value.Y, Z = value.Z, W = value.W };

        // A frame's envelope with its kind's name and no payload; a link adds the payload it carries.
        public static PbSim.SensorFrame ToProtoEnvelope(ISensorFrame frame, string kindName)
        {
            if (!frame.HasValidEnvelope)
            {
                throw new InvalidOperationException($"Frame {frame.Sequence} of sensor '{frame.SensorId}' has an invalid envelope.");
            }

            return new PbSim.SensorFrame
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
                Kind = kindName
            };
        }

        // Core Valid is 0, but the wire's 0 is UNSPECIFIED, so every status maps explicitly.
        public static PbSim.SensorFrameStatus ToProto(SensorFrameStatus status) => status switch
        {
            SensorFrameStatus.Valid => PbSim.SensorFrameStatus.Valid,
            SensorFrameStatus.Unavailable => PbSim.SensorFrameStatus.Unavailable,
            SensorFrameStatus.Stale => PbSim.SensorFrameStatus.Stale,
            SensorFrameStatus.Invalid => PbSim.SensorFrameStatus.Invalid,
            _ => throw new ArgumentException($"Sensor frame status {status} has no wire form.", nameof(status))
        };

        public static PbSim.Matrix3 ToProto(Matrix3d value) => new PbSim.Matrix3
        {
            Row0 = ToProto(value.Row0),
            Row1 = ToProto(value.Row1),
            Row2 = ToProto(value.Row2)
        };
    }
}
