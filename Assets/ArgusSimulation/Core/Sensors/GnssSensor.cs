namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not registered, not referenced.
    // TODO(sensors): GNSS receiver.
    // - Measurement: position and velocity in ITRF93, receiver time, fix status, satellites used.
    // - Profile: output rate, position and velocity noise, time to first fix, outage behaviour.
    // - Source open: Basilisk has no GNSS receiver module. Choose between simpleNav (NavTransMsg) as
    //   a Basilisk-sourced BackendSensorModel (D10) and an Argus-computed SensorModel<T> from truth.
    // - Then add a kind (an IDeviceKind like ImuKind) and its registration in DeviceKinds, plus, if
    //   Basilisk models it, a service module (docs/device-model.md §7). Needs the flight part from
    //   the hardware team.
    internal sealed class GnssSensor
    {
    }
}
