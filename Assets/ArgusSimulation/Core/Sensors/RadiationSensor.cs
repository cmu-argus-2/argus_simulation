namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not registered, not referenced.
    // TODO(sensors): radiation monitor.
    // - Measurement: to be defined with the payload team (dose rate, particle flux or upsets).
    // - Source open. Basilisk dentonFluxModel is not a candidate: it models GEO plasma flux
    //   (PlasmaFluxMsg) and is invalid in LEO. Decide the LEO model (for example trapped-particle
    //   flux and dose) and whether it runs in Basilisk (D10) or in Argus.
    internal sealed class RadiationSensor
    {
    }
}
