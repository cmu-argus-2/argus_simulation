using System;

namespace Argus.Simulation.Core
{
    // The camera kind's controller projection (D7, CORE-23): ImageFrame in, CameraObservation out.
    // A controller gets the camera ID, the frame's sequence, the exposure start and length, and a
    // copy of the pixels; the pose and the Sun in the ImageFrame's RenderRequest stay with the
    // recorder. The envelope is kept (ControllerProjection), so the controller's frame joins the
    // recorded one; the status is set here.
    //
    // A Valid frame keeps its pixels only when it is a valid Rgb8 image whose request has a Sun
    // position. Any other encoding could carry more than pixels (an alpha channel, PNG text chunks),
    // and an image with no Sun was lit by a light that isn't the run's (analytic runs; G3: never
    // approximated). A frame without pixels is Unavailable, with size 0, except that an Invalid
    // frame stays Invalid, so a controller can tell a fault from a frame it may not see.
    public sealed class CameraControllerProjection : ControllerProjection
    {
        public static readonly CameraControllerProjection Instance = new CameraControllerProjection();

        private CameraControllerProjection()
        {
        }

        public override Type InputPayloadType => typeof(ImageFrame);
        public override Type OutputPayloadType => typeof(CameraObservation);

        protected override ISensorFrame ProjectCore(ISensorFrame frame)
        {
            SensorFrame<ImageFrame> camera = (SensorFrame<ImageFrame>)frame;
            ImageFrame image = camera.Payload;
            RenderRequest request = image.Request;
            bool hasPixels =
                camera.Status == SensorFrameStatus.Valid &&
                image.IsValid &&
                image.Encoding == ImageEncoding.Rgb8 &&
                request.HasSunPosition;
            CameraObservation observation = new CameraObservation(
                request.CameraId,
                request.Sequence,
                request.ExposureStartSimulationTimeSeconds,
                request.ExposureStartUtc,
                request.ExposureDurationSeconds,
                hasPixels ? request.Intrinsics.WidthPixels : 0,
                hasPixels ? request.Intrinsics.HeightPixels : 0,
                hasPixels ? new ReadOnlyMemory<byte>(CopyOf(image.Data)) : ReadOnlyMemory<byte>.Empty);

            return new SensorFrame<CameraObservation>(
                camera.RunId,
                camera.SensorId,
                camera.FrameId,
                camera.Sequence,
                camera.StateSequence,
                camera.SimulationTimeSeconds,
                camera.TimestampUtc,
                hasPixels ? SensorFrameStatus.Valid
                    : camera.Status == SensorFrameStatus.Invalid ? SensorFrameStatus.Invalid
                    : SensorFrameStatus.Unavailable,
                camera.Source,
                observation);
        }

        // A controller gets its own array. ReadOnlyMemory can still be written through
        // MemoryMarshal.TryGetArray, and the recorder queues the frame by reference (#99), so a
        // shared array would let a controller change what the recorder writes (D6, D7).
        private static byte[] CopyOf(byte[] data)
        {
            byte[] copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            return copy;
        }
    }
}
