using System;

namespace Argus.Simulation.Core
{
    public enum ImageEncoding
    {
        Rgb8 = 0,
        Rgba8 = 1,
        Jpeg = 2,
        Png = 3
    }

    // One rendered exposure: the pixels plus the RenderRequest they were rendered for. Every pose,
    // calibration and timing field comes from the request, so the camera model, not the renderer,
    // owns them (D5).
    public readonly struct ImageFrame
    {
        public ImageFrame(
            RenderRequest request,
            ImageEncoding encoding,
            byte[] data,
            string renderer)
        {
            Request = request;
            Encoding = encoding;
            Data = data;
            Renderer = renderer;
        }

        public RenderRequest Request { get; }
        public ImageEncoding Encoding { get; }
        public byte[] Data { get; }
        public string Renderer { get; }

        public string CameraId => Request.CameraId;
        public long Sequence => Request.Sequence;
        public long StateSequence => Request.StateSequence;
        public double SimulationTimeSeconds => Request.ExposureStartSimulationTimeSeconds;
        public DateTimeOffset TimestampUtc => Request.ExposureStartUtc;
        public double ExposureDurationSeconds => Request.ExposureDurationSeconds;
        public CameraIntrinsics Intrinsics => Request.Intrinsics;
        public Vector3d CameraPositionEcefMeters => Request.CameraPositionEcefMeters;
        public Quaterniond CameraToEcef => Request.CameraToEcef;

        public bool IsValid =>
            Request.IsValid &&
            Data != null &&
            Data.Length > 0 &&
            HasExpectedRawLength &&
            !string.IsNullOrWhiteSpace(Renderer);

        // Raw encodings hold exactly width x height pixels; compressed ones are not checked.
        private bool HasExpectedRawLength
        {
            get
            {
                long pixels = (long)Intrinsics.WidthPixels * Intrinsics.HeightPixels;
                switch (Encoding)
                {
                    case ImageEncoding.Rgb8:
                        return Data.LongLength == pixels * 3;
                    case ImageEncoding.Rgba8:
                        return Data.LongLength == pixels * 4;
                    default:
                        return true;
                }
            }
        }
    }
}
