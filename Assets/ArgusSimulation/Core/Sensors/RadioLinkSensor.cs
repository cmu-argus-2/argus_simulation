namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not registered, not referenced.
    // TODO(sensors): radio link status.
    // - Measurement: ground-station visibility, elevation, range, link margin.
    // - Source open: Basilisk groundLocation (AccessMsg) and linkBudget (LinkBudgetMsg, which needs
    //   AntennaLogMsg inputs) as a BackendSensorModel (D10). linkBudget reports CNR, so the margin
    //   also needs a required-CNR threshold.
    // - Needs ground stations and radio parameters from the communications team.
    internal sealed class RadioLinkSensor
    {
    }
}
