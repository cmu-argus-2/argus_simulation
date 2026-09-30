namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not registered, not referenced.
    // TODO(sensors): power telemetry.
    // - Measurement: stored energy and capacity, net power, solar-array power; bus voltage and
    //   current if the flight telemetry reports them.
    // - Source open: Basilisk power modules (simpleSolarPanel, simpleBattery, simplePowerMonitor;
    //   PowerStorageStatusMsg, PowerNodeUsageMsg) model energy and power only, as a
    //   BackendSensorModel (D10). Voltage and current would need an Argus model.
    // - Needs the power budget (panels, battery, loads) from the hardware team.
    internal sealed class PowerTelemetrySensor
    {
    }
}
