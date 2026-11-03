namespace Argus.Simulation.Core
{
    // A sensor kind whose sensor is a camera, with a CameraProfile (CORE-6): the recorder lists it in
    // the manifest's cameras, and cameras that share a profile ID must share its parameter set. A kind
    // whose sensor is a CameraModel must implement it (DeviceKindsTests), so a camera kind added later,
    // such as CORE-8's nadir camera (#44), cannot slip past those rules.
    public interface ICameraKind : IDeviceKind
    {
        // The profile of a valid configuration of this kind; throws ArgumentException otherwise.
        CameraProfile Profile(DeviceConfiguration configuration);
    }
}
