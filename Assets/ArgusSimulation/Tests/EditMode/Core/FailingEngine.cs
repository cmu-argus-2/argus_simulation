using System;
using Argus.Simulation.Core;

namespace Argus.Simulation.Tests
{
    // The analytic engine, throwing the given exception from TryStep at one step.
    internal sealed class FailingEngine : ISimulationEngine
    {
        private readonly ISimulationEngine _inner;
        private readonly long _failAtStep;
        private readonly Exception _failure;

        public FailingEngine(ISimulationEngine inner, long failAtStep, Exception failure)
        {
            _inner = inner;
            _failAtStep = failAtStep;
            _failure = failure;
        }

        public string BackendName => _inner.BackendName;
        public bool IsInitialized => _inner.IsInitialized;
        public void Initialize(SimulationConfiguration configuration) => _inner.Initialize(configuration);
        public bool TryReset(out SimulationState state) => _inner.TryReset(out state);

        public bool TryStep(SimulationStepInput input, out SimulationState state)
        {
            if (input.Sequence + 1 == _failAtStep)
            {
                throw _failure;
            }

            return _inner.TryStep(input, out state);
        }
    }
}
