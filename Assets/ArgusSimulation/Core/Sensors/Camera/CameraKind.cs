namespace Argus.Simulation.Core
{
    // The "camera" kind, for the body cameras. A specific part, such as the Arducam IMX708, is a
    // parameter set for it (D11): its parameters are the DATA-6 (#71) profile (CameraProfileKind,
    // CORE-6). Its frames carry the true pose they were rendered for, so a controller may not see
    // them (D7).
    // TODO(D7): a controller-safe camera observation without pose or Sun is CORE-23 (#125).
    public sealed class CameraKind : CameraProfileKind
    {
        public const string KindName = "camera";

        public static readonly CameraKind Instance = new CameraKind();

        private CameraKind()
            : base(KindName, mayBeControllerVisible: false)
        {
        }

        // Without a renderer, every exposure is Unavailable (D5).
        public override ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer) =>
            new BodyCameraModel(configuration, imageRenderer);
    }
}
