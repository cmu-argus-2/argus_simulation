namespace Argus.Simulation.Core
{
    // The "camera" kind, for the body cameras. A specific part, such as the Arducam IMX708, is a
    // parameter set for it (D11): its parameters are the DATA-6 (#71) profile (CameraProfileKind,
    // CORE-6). Its frames carry the true pose and Sun they were rendered for, so a controller sees
    // them only through CameraControllerProjection: a CameraObservation with the pixels and timing,
    // without the pose or the Sun (D7, CORE-23).
    public sealed class CameraKind : CameraProfileKind
    {
        public const string KindName = "camera";

        public static readonly CameraKind Instance = new CameraKind();

        private CameraKind()
            : base(KindName, CameraControllerProjection.Instance)
        {
        }

        // Without a renderer, every exposure is Unavailable (D5).
        public override ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer) =>
            new BodyCameraModel(configuration, imageRenderer);
    }
}
