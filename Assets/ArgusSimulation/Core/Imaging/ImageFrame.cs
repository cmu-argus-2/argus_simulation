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

    public readonly struct ImageFrame
    {
        public ImageFrame(
            string cameraId,
            long sequence,
            double simulationTimeSeconds,
            DateTimeOffset timestampUtc,
            CameraIntrinsics intrinsics,
            Vector3d cameraPositionEcefMeters,
            Quaterniond cameraToEcef,
            ImageEncoding encoding,
            byte[] data,
            string renderer)
        {
            CameraId = cameraId;
            Sequence = sequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            TimestampUtc = timestampUtc.ToUniversalTime();
            Intrinsics = intrinsics;
            CameraPositionEcefMeters = cameraPositionEcefMeters;
            CameraToEcef = cameraToEcef;
            Encoding = encoding;
            Data = data;
            Renderer = renderer;
        }

        public string CameraId { get; }
        public long Sequence { get; }
        public double SimulationTimeSeconds { get; }
        public DateTimeOffset TimestampUtc { get; }
        public CameraIntrinsics Intrinsics { get; }
        public Vector3d CameraPositionEcefMeters { get; }
        public Quaterniond CameraToEcef { get; }
        public ImageEncoding Encoding { get; }
        public byte[] Data { get; }
        public string Renderer { get; }

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(CameraId) &&
            Sequence >= 0 &&
            SimulationTimeSeconds >= 0.0 &&
            !double.IsNaN(SimulationTimeSeconds) &&
            !double.IsInfinity(SimulationTimeSeconds) &&
            Intrinsics.IsValid &&
            CameraPositionEcefMeters.IsFinite &&
            CameraToEcef.IsUnit &&
            Data != null &&
            Data.Length > 0 &&
            !string.IsNullOrWhiteSpace(Renderer);
    }
}
