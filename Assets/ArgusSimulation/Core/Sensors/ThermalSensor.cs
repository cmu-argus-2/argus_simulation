namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not registered, not referenced.
    // TODO(sensors): temperature sensors.
    // - Measurement: temperature at each named sensor location, in kelvin (SI).
    // - Source open: Basilisk sensorThermal (TemperatureMsg) as a BackendSensorModel (D10), whose
    //   temperature is in Celsius, so the mapper converts; or an Argus thermal model. Confirm it
    //   fits the spacecraft's thermal design first.
    // - Needs sensor locations and thermal properties from the hardware team.
    internal sealed class ThermalSensor
    {
    }
}
