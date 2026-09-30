namespace Argus.Simulation.Core
{
    // A state source whose caller drives time, as the Unity runner does for the analytic fixture in
    // development runs. Followers of a Basilisk run implement only ISimulationStateSource.
    public interface IStepDrivenStateSource : ISimulationStateSource
    {
        // Produces the state at this step and raises StateProduced once, synchronously, before
        // returning true; returns false without raising it if it cannot.
        bool TryStep(long sequence, double simulationTimeSeconds);

        // Starts a new run with a new run ID; the next step is sequence 0.
        void ResetRun();
    }
}
