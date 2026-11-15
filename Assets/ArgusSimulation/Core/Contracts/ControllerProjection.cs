using System;

namespace Argus.Simulation.Core
{
    // What a controller sees of one device's frames (D7). Each kind that may be controller-visible
    // declares one (IDeviceKind.ControllerProjection), and the gateway applies it to every frame of a
    // device configured as visible. A projection takes frames of one payload type and returns frames
    // of another, or the same, and keeps the envelope, so a controller's frame joins the recorded
    // frame it came from. The envelope is a frame's run ID, sensor ID, frame ID, sequence, state
    // sequence, simulation time, UTC and source. The status is not part of it: a projection keeps
    // the status, or makes the frame Unavailable to withhold its payload; an Invalid frame stays
    // Invalid. Any other change would show a controller a payload its sensor didn't produce (Valid
    // and Stale carry one) or hide a fault (Invalid). A kind whose frames carry no truth declares
    // the identity on its payload; a camera drops the pose and the Sun (CameraControllerProjection,
    // CORE-23).
    //
    // Project checks both payload types and the envelope, so a frame reaches a controller only as the
    // type its kind declared. Its messages can reach a controller as a termination reason, so they
    // name sensor IDs, types and envelope fields, never payload values.
    public abstract class ControllerProjection
    {
        public abstract Type InputPayloadType { get; }
        public abstract Type OutputPayloadType { get; }

        // Passes frames of T through unchanged, for a kind whose frames carry no truth.
        public static ControllerProjection Identity<T>() where T : struct => IdentityProjection<T>.Instance;

        public ISensorFrame Project(ISensorFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.PayloadType != InputPayloadType)
            {
                throw new InvalidOperationException(
                    $"Sensor '{frame.SensorId}' published a {frame.PayloadType.Name} payload; " +
                    $"its kind's controller projection takes {InputPayloadType.Name}.");
            }

            ISensorFrame projected = ProjectCore(frame);
            if (projected == null || projected.PayloadType != OutputPayloadType)
            {
                throw new InvalidOperationException(
                    $"The controller projection of sensor '{frame.SensorId}' returned " +
                    $"{(projected == null ? "no frame" : $"a {projected.PayloadType.Name} payload")}, " +
                    $"not its declared {OutputPayloadType.Name}.");
            }

            string changed = ChangedEnvelopeField(frame, projected);
            if (changed != null)
            {
                throw new InvalidOperationException(
                    $"The controller projection of sensor '{frame.SensorId}' changed the frame's {changed}.");
            }

            if (projected.Status != frame.Status &&
                (projected.Status != SensorFrameStatus.Unavailable || frame.Status == SensorFrameStatus.Invalid))
            {
                throw new InvalidOperationException(
                    $"The controller projection of sensor '{frame.SensorId}' changed a {frame.Status} frame " +
                    $"to {projected.Status}.");
            }

            return projected;
        }

        // The controller's frame for one frame whose payload is InputPayloadType. It must keep the
        // envelope, and keeps the status or makes the frame Unavailable when it drops the payload.
        protected abstract ISensorFrame ProjectCore(ISensorFrame frame);

        // The status is not part of the envelope: Project checks it separately.
        private static string ChangedEnvelopeField(ISensorFrame frame, ISensorFrame projected)
        {
            if (!string.Equals(frame.RunId, projected.RunId, StringComparison.Ordinal))
            {
                return nameof(ISensorFrame.RunId);
            }

            if (!string.Equals(frame.SensorId, projected.SensorId, StringComparison.Ordinal))
            {
                return nameof(ISensorFrame.SensorId);
            }

            if (!string.Equals(frame.FrameId, projected.FrameId, StringComparison.Ordinal))
            {
                return nameof(ISensorFrame.FrameId);
            }

            if (frame.Sequence != projected.Sequence)
            {
                return nameof(ISensorFrame.Sequence);
            }

            if (frame.StateSequence != projected.StateSequence)
            {
                return nameof(ISensorFrame.StateSequence);
            }

            if (!frame.SimulationTimeSeconds.Equals(projected.SimulationTimeSeconds))
            {
                return nameof(ISensorFrame.SimulationTimeSeconds);
            }

            if (!frame.TimestampUtc.Equals(projected.TimestampUtc))
            {
                return nameof(ISensorFrame.TimestampUtc);
            }

            if (!string.Equals(frame.Source, projected.Source, StringComparison.Ordinal))
            {
                return nameof(ISensorFrame.Source);
            }

            return null;
        }

        private sealed class IdentityProjection<T> : ControllerProjection
            where T : struct
        {
            public static readonly IdentityProjection<T> Instance = new IdentityProjection<T>();

            public override Type InputPayloadType => typeof(T);
            public override Type OutputPayloadType => typeof(T);

            protected override ISensorFrame ProjectCore(ISensorFrame frame) => frame;
        }
    }
}
