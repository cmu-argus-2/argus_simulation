using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Argus.Simulation.Core
{
    // Base camera sensor (D5). It owns exposure timing, pose, intrinsics and exposure duration, asks
    // an IImageRenderer for each exposure, and publishes the image as a SensorFrame<ImageFrame>.
    // Distortion and noise come with the concrete camera profiles (CORE-6, CORE-8).
    //
    // Rendering is asynchronous and TrySample is not, so every exposure is deferred: it starts at its
    // slot and is published on a later step, still stamped with its capture state and time (G3).
    // Frames are published in sequence order. An exposure that is not rendered within
    // MaximumLatencySeconds of simulation time, or whose render fails, is published as Unavailable;
    // a camera never publishes a black or approximated image. TrySample never waits for a render.
    public abstract class CameraModel : SensorModel<ImageFrame>
    {
        private const double TimeToleranceSeconds = 1e-9;

        private readonly IImageRenderer _renderer;
        private readonly Queue<PendingExposure> _pending = new Queue<PendingExposure>();

        protected CameraModel(
            SensorDefinition definition,
            CameraIntrinsics intrinsics,
            double exposureDurationSeconds,
            double maximumLatencySeconds,
            IImageRenderer renderer)
            : base(definition)
        {
            if (!intrinsics.IsValid)
            {
                throw new ArgumentException("Valid camera intrinsics are required.", nameof(intrinsics));
            }

            if (!(exposureDurationSeconds >= 0.0) || double.IsInfinity(exposureDurationSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(exposureDurationSeconds), "Must be finite and >= 0.");
            }

            if (!(maximumLatencySeconds > 0.0))
            {
                throw new ArgumentOutOfRangeException(nameof(maximumLatencySeconds), "Must be > 0 (infinity waits for every render).");
            }

            Intrinsics = intrinsics;
            ExposureDurationSeconds = exposureDurationSeconds;
            MaximumLatencySeconds = maximumLatencySeconds;
            _renderer = renderer;
        }

        public CameraIntrinsics Intrinsics { get; }
        public double ExposureDurationSeconds { get; }

        // How far simulation time may pass an exposure before it is given up as Unavailable.
        // PositiveInfinity waits for every render, for runs whose step loop waits for the camera.
        public double MaximumLatencySeconds { get; }

        // Exposures started but not yet published; a step loop that must not outrun the renderer
        // (lockstep runs) can wait until this is 0.
        public int PendingExposureCount => _pending.Count;

        // Camera position and orientation for an exposure. The default is body-mounted: the mount's
        // lever arm and rotation applied to the spacecraft pose. The nadir ground-truth camera
        // (CORE-8) overrides this to ignore attitude.
        protected virtual void ComputePose(
            SpacecraftState spacecraft,
            out Vector3d cameraPositionEcefMeters,
            out Quaterniond cameraToEcef)
        {
            SensorMount mount = Definition.Mount;
            cameraPositionEcefMeters =
                spacecraft.PositionEcefMeters + spacecraft.BodyToEcef.Rotate(mount.PositionBodyMeters);
            cameraToEcef = (spacecraft.BodyToEcef * mount.SensorToBody).Normalized();
        }

        protected override void OnReset(SensorResetContext context)
        {
            while (_pending.Count > 0)
            {
                Abandon(_pending.Dequeue());
            }
        }

        // Only reached if a slot is not deferred, which never happens; kept Unavailable.
        protected override SensorFrameStatus Measure(SensorSampleContext context, out ImageFrame payload)
        {
            payload = default;
            return SensorFrameStatus.Unavailable;
        }

        protected override bool TryDefer(SensorSampleContext context, SensorSlot slot)
        {
            ComputePose(context.Spacecraft, out Vector3d position, out Quaterniond orientation);
            Vector3d? sun = context.Environment.HasValue
                ? context.Environment.Value.SunPositionItrf93Meters
                : (Vector3d?)null;
            RenderRequest request = new RenderRequest(
                Definition.SensorId,
                slot.Sequence,
                slot.StateSequence,
                slot.SimulationTimeSeconds,
                slot.TimestampUtc,
                ExposureDurationSeconds,
                position,
                orientation,
                Intrinsics,
                sun);

            PendingExposure exposure = new PendingExposure(slot, request);
            if (_renderer != null && request.IsValid)
            {
                exposure.Cancellation = new CancellationTokenSource();
                try
                {
                    exposure.Render = _renderer.RenderAsync(request, exposure.Cancellation.Token);
                }
                catch (Exception)
                {
                    // A renderer that throws before returning a task leaves this exposure Unavailable.
                    exposure.Render = null;
                }
            }

            _pending.Enqueue(exposure);
            return true;
        }

        protected override bool TryCompleteDeferred(
            SensorSampleContext context,
            out SensorSlot slot,
            out SensorFrameStatus status,
            out ImageFrame payload)
        {
            slot = default;
            status = SensorFrameStatus.Unavailable;
            payload = default;
            if (_pending.Count == 0)
            {
                return false;
            }

            PendingExposure head = _pending.Peek();
            Task<ImageFrame> render = head.Render;
            if (render != null && !render.IsCompleted)
            {
                double latency = context.Spacecraft.SimulationTimeSeconds - head.Slot.SimulationTimeSeconds;
                if (latency <= MaximumLatencySeconds + TimeToleranceSeconds)
                {
                    return false;
                }

                _pending.Dequeue();
                Abandon(head);
                slot = head.Slot;
                payload = WithoutPixels(head.Request);
                return true;
            }

            _pending.Dequeue();
            slot = head.Slot;
            if (render != null && render.Status == TaskStatus.RanToCompletion &&
                TryAccept(head.Request, render.Result, out ImageFrame accepted))
            {
                status = SensorFrameStatus.Valid;
                payload = accepted;
            }
            else
            {
                Observe(render);
                payload = WithoutPixels(head.Request);
            }

            head.Cancellation?.Dispose();
            return true;
        }

        // The renderer supplies only pixels; the published frame carries this model's request.
        private static bool TryAccept(RenderRequest request, ImageFrame rendered, out ImageFrame frame)
        {
            frame = new ImageFrame(request, rendered.Encoding, rendered.Data, rendered.Renderer);
            return string.Equals(rendered.CameraId, request.CameraId, StringComparison.Ordinal) &&
                rendered.Sequence == request.Sequence &&
                frame.IsValid;
        }

        // An Unavailable exposure keeps its request, so it still names its camera, pose and capture
        // state; it has no pixels and no renderer.
        private static ImageFrame WithoutPixels(RenderRequest request) =>
            new ImageFrame(request, default, null, null);

        // Not disposed: the renderer may still hold the token.
        private static void Abandon(PendingExposure exposure)
        {
            exposure.Cancellation?.Cancel();
            Observe(exposure.Render);
        }

        // Marks a failed or abandoned render's exception as observed, now or when it finishes.
        private static void Observe(Task<ImageFrame> render)
        {
            render?.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private sealed class PendingExposure
        {
            public PendingExposure(SensorSlot slot, RenderRequest request)
            {
                Slot = slot;
                Request = request;
            }

            public SensorSlot Slot { get; }
            public RenderRequest Request { get; }
            public Task<ImageFrame> Render { get; set; }
            public CancellationTokenSource Cancellation { get; set; }
        }
    }
}
