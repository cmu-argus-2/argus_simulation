using System;

namespace Argus.Simulation.Core
{
    // Delivers whole SimulationStates in increasing sequence order within a run; a follower of the
    // decimated host stream (G2) may skip steps. In Basilisk runs Basilisk owns simulation time (D2)
    // and the source only follows it. An IStepDrivenStateSource (the analytic fixture, D9) is
    // stepped by its caller, which then owns the clock.
    public interface ISimulationStateSource
    {
        event Action<SimulationState> StateProduced;
    }
}
