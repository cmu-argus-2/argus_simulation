namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not registered, not referenced.
    // TODO(sensors): star tracker.
    // - Measurement: attitude of the tracker case relative to J2000 (Hamilton, scalar last).
    // - Profile: cross-boresight and roll noise, output rate, Sun/Earth exclusion angles.
    // - Source open: Basilisk starTracker (STSensorMsgPayload.qInrtl2Case, scalar first) as a
    //   BackendSensorModel (D10), or Argus-computed. starTracker 2.11.1 models no Sun or Earth
    //   exclusion, so exclusion and outages need Argus or service-side logic either way.
    // - Then add a SensorKind value, a profile in sensors.proto, a measurement in the Basilisk
    //   StepResponse and a SensorFactory case, as for ImuSensor.
    internal sealed class StarTrackerSensor
    {
    }
}
