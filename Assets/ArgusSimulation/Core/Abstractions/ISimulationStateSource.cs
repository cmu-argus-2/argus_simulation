using System;

namespace Argus.Simulation.Core
{
    // Delivers whole SimulationStates in increasing sequence order within a run. The headless host
    // owns the run (G2): in Basilisk runs Basilisk owns simulation time (D2), and a source only
    // follows it, so a follower of the decimated host stream may skip steps.
    public interface ISimulationStateSource
    {
        event Action<SimulationState> StateProduced;

        // Raised before the first state of a new run or subscription, whose sequence may restart:
        // consumers clear interpolation and history built from earlier states.
        event Action HistoryReset;
    }
}
