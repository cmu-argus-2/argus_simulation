namespace Argus.Simulation.Core
{
    // Basilisk, recorded trajectories, and deterministic analytic scenarios all implement this contract.
    // TODO(G2): replace with ISimulationSnapshotSource, which carries the whole snapshot.
    public interface ISpacecraftStateSource
    {
        bool TryGetState(long sequence, double simulationTimeSeconds, out SpacecraftState state);
    }
}
