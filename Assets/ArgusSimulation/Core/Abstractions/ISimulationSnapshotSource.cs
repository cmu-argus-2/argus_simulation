namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not referenced.
    // TODO(G2): replaces ISpacecraftStateSource so consumers receive whole SimulationSnapshots
    // (spacecraft, environment, sensor measurements, applied commands) instead of only
    // SpacecraftState. Implementations: the Unity snapshot-stream client (follower of the headless
    // host) and an analytic source wrapping AnalyticSimulationEngine for development.
    internal interface ISimulationSnapshotSource
    {
    }
}
