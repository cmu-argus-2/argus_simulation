namespace Argus.Simulation.Core
{
    // Basilisk, recorded trajectories, and deterministic analytic scenarios all implement this contract.
    // TODO(G2): replace with ISimulationStateSource, which carries the whole SimulationState.
    public interface ISpacecraftStateSource
    {
        bool TryGetState(long sequence, double simulationTimeSeconds, out SpacecraftState state);
    }
}
