namespace Argus.Simulation.Core
{
    // Basilisk, recorded trajectories, and deterministic analytic scenarios all implement this contract.
    public interface ISpacecraftStateSource
    {
        string SourceName { get; }
        bool TryGetState(long sequence, double simulationTimeSeconds, out SpacecraftState state);
    }
}
