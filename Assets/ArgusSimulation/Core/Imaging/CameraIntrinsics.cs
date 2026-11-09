namespace Argus.Simulation.Core
{
    // Pinhole calibration in pixel units. Distortion remains part of the camera model/profile.
    // Pixels follow OpenCV: u runs right along a row and v down a column, and (0, 0) is the centre
    // of the top-left pixel, so the geometric image centre is ((W - 1) / 2, (H - 1) / 2). A pixel
    // (u, v) looks along the camera-frame ray ((u - cx) / fx, (v - cy) / fy, 1); see RenderRequest.
    public readonly struct CameraIntrinsics
    {
        public CameraIntrinsics(
            int widthPixels,
            int heightPixels,
            double focalLengthXPixels,
            double focalLengthYPixels,
            double principalPointXPixels,
            double principalPointYPixels)
        {
            WidthPixels = widthPixels;
            HeightPixels = heightPixels;
            FocalLengthXPixels = focalLengthXPixels;
            FocalLengthYPixels = focalLengthYPixels;
            PrincipalPointXPixels = principalPointXPixels;
            PrincipalPointYPixels = principalPointYPixels;
        }

        public int WidthPixels { get; }
        public int HeightPixels { get; }
        public double FocalLengthXPixels { get; }
        public double FocalLengthYPixels { get; }
        public double PrincipalPointXPixels { get; }
        public double PrincipalPointYPixels { get; }

        public bool IsValid =>
            WidthPixels > 0 &&
            HeightPixels > 0 &&
            FocalLengthXPixels > 0.0 &&
            FocalLengthYPixels > 0.0 &&
            IsFinite(FocalLengthXPixels) &&
            IsFinite(FocalLengthYPixels) &&
            IsFinite(PrincipalPointXPixels) &&
            IsFinite(PrincipalPointYPixels);

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
