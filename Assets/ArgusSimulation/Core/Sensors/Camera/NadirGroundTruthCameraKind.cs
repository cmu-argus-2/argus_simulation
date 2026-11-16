namespace Argus.Simulation.Core
{
    // The "nadir_ground_truth_camera" kind (CORE-8): the north-up nadir reference view, with the
    // same DATA-6 parameters as a body camera (CameraProfileKind). It is ground truth, so a controller
    // never sees it (D7): its frames reach the recorder, never a gateway observation. Its profile_id
    // must differ from the body cameras', since cameras that share an ID share their parameters.
    public sealed class NadirGroundTruthCameraKind : CameraProfileKind
    {
        public const string KindName = "nadir_ground_truth_camera";

        public static readonly NadirGroundTruthCameraKind Instance = new NadirGroundTruthCameraKind();

        private NadirGroundTruthCameraKind()
            : base(KindName, mayBeControllerVisible: false)
        {
        }

        // Without a renderer, every exposure is Unavailable (D5).
        public override ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer) =>
            new NadirGroundTruthCameraModel(configuration, imageRenderer);
    }
}
