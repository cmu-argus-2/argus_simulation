using System;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Tests
{
    // A host stand-in for PlayMode tests: publishes states of Core's analytic test fixture (D9) as
    // StateStreamClient would, on demand.
    public sealed class TestStateSource : MonoBehaviour, ISimulationStateSource
    {
        private AnalyticSimulationEngine _engine;

        public event Action<SimulationState> StateProduced;
        public event Action HistoryReset;

        // Publishes the fixture's state at this step of a 0.1 s run.
        public SimulationState Publish(long sequence)
        {
            if (_engine == null)
            {
                _engine = new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0);
                _engine.Initialize(new SimulationConfiguration("test-run", DateTimeOffset.UnixEpoch, 0.1));
            }

            if (!_engine.TrySample(sequence, sequence * 0.1, out SimulationState state))
            {
                throw new InvalidOperationException($"The analytic fixture has no state at step {sequence}.");
            }

            StateProduced?.Invoke(state);
            return state;
        }

        public void ResetHistory() => HistoryReset?.Invoke();
    }
}
