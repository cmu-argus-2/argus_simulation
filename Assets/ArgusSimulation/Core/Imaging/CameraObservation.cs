using System;

namespace Argus.Simulation.Core
{
    // What a controller sees of one camera exposure (D7, CORE-23): what flight's own frame record
    // holds (FSW-Payload include/vision/frame.hpp), the camera ID, the frame's sequence, the exposure
    // start and length, and the image. Nothing here can be truth: no pose, no Sun, no intrinsics and
    // no renderer. A controller takes intrinsics and mounts from the spacecraft profile, as flight
    // takes them from its config.toml. CameraControllerProjection builds it from an ImageFrame.
    // CameraContractTests pins its instance fields and public instance properties; change them only
    // with the decision. IsValid is computed and stores nothing.
    public readonly struct CameraObservation
    {
        public CameraObservation(
            string cameraId,
            long sequence,
            double exposureStartSimulationTimeSeconds,
            DateTimeOffset exposureStartUtc,
            double exposureDurationSeconds,
            int widthPixels,
            int heightPixels,
            ReadOnlyMemory<byte> pixels)
        {
            CameraId = cameraId;
            Sequence = sequence;
            ExposureStartSimulationTimeSeconds = exposureStartSimulationTimeSeconds;
            ExposureStartUtc = exposureStartUtc.ToUniversalTime();
            ExposureDurationSeconds = exposureDurationSeconds;
            WidthPixels = widthPixels;
            HeightPixels = heightPixels;
            Pixels = pixels;
        }

        public string CameraId { get; }

        // The camera's frame sequence.
        public long Sequence { get; }

        public double ExposureStartSimulationTimeSeconds { get; }
        public DateTimeOffset ExposureStartUtc { get; }
        public double ExposureDurationSeconds { get; }

        // 0 when there are no pixels.
        public int WidthPixels { get; }
        public int HeightPixels { get; }

        // Raw Rgb8, row-major from the top-left pixel, 3 bytes per pixel: the layout of an Rgb8
        // ImageFrame.Data. CameraControllerProjection gives each observation a copy of its own, so
        // writing through MemoryMarshal.TryGetArray can't change the recorded frame. Empty when the
        // frame is not Valid.
        public ReadOnlyMemory<byte> Pixels { get; }

        // Names its camera, has a non-negative sequence and a finite, non-negative exposure start
        // and duration (0 is a valid exposure, as in RenderRequest), and either no pixels at size 0
        // or exactly width x height x 3 bytes.
        public bool IsValid =>
            !string.IsNullOrWhiteSpace(CameraId) &&
            Sequence >= 0 &&
            ExposureStartSimulationTimeSeconds >= 0.0 &&
            !double.IsInfinity(ExposureStartSimulationTimeSeconds) &&
            ExposureDurationSeconds >= 0.0 &&
            !double.IsInfinity(ExposureDurationSeconds) &&
            (Pixels.IsEmpty
                ? WidthPixels == 0 && HeightPixels == 0
                : WidthPixels > 0 && HeightPixels > 0 && Pixels.Length == (long)WidthPixels * HeightPixels * 3);
    }
}
