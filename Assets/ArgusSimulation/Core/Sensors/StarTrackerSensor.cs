namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not registered, not referenced.
    // TODO(sensors): star tracker.
    // - Measurement: attitude of the tracker case relative to J2000 (Hamilton, scalar last).
    // - Profile: cross-boresight and roll noise, output rate, Sun/Earth exclusion angles.
    // - Source open: Basilisk starTracker (STSensorMsgPayload.qInrtl2Case, scalar first) as a
    //   BackendSensorModel (D10), or Argus-computed. starTracker in bsk 2.12.2 models no Sun or Earth
    //   exclusion, so exclusion and outages need Argus or service-side logic either way.
    // - Then add a kind (an IDeviceKind like ImuKind) and its registration in DeviceKinds, plus, if
    //   Basilisk models it, a service module with its output keys (docs/device-model.md §7).
    internal sealed class StarTrackerSensor
    {
    }
}
