using System;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    public sealed class SimulationRunner : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour stateSourceComponent;
        [SerializeField, Min(0.001f)] private double fixedStepSeconds = 0.1;
        [SerializeField, Min(0.0f)] private double timeScale = 1.0;
        [SerializeField] private bool runOnStart = true;
        [SerializeField, Min(1)] private int maximumStepsPerFrame = 100;

        private ISpacecraftStateSource _stateSource;
        private double _accumulatorSeconds;
        private double _simulationTimeSeconds;
        private long _sequence;

        public event Action<SpacecraftState> StateProduced;

        public bool IsRunning { get; set; }
        public double SimulationTimeSeconds => _simulationTimeSeconds;

        private void Awake()
        {
            ResolveStateSource();
            IsRunning = runOnStart;
        }

        private void Update()
        {
            if (!IsRunning || _stateSource == null)
            {
                return;
            }

            _accumulatorSeconds += Time.unscaledDeltaTime * timeScale;
            int steps = 0;
            while (_accumulatorSeconds >= fixedStepSeconds && steps < maximumStepsPerFrame)
            {
                StepOnce();
                _accumulatorSeconds -= fixedStepSeconds;
                steps++;
            }
        }

        public void Configure(MonoBehaviour source, double stepSeconds = 0.1)
        {
            stateSourceComponent = source;
            fixedStepSeconds = Math.Max(0.001, stepSeconds);
            ResolveStateSource();
        }

        public bool StepOnce()
        {
            if (_stateSource == null)
            {
                ResolveStateSource();
            }

            if (_stateSource == null ||
                !_stateSource.TryGetState(_sequence, _simulationTimeSeconds, out SpacecraftState state) ||
                !state.IsValid)
            {
                return false;
            }

            StateProduced?.Invoke(state);
            _sequence++;
            _simulationTimeSeconds += fixedStepSeconds;
            return true;
        }

        public void ResetSimulation()
        {
            _accumulatorSeconds = 0.0;
            _simulationTimeSeconds = 0.0;
            _sequence = 0;
        }

        private void ResolveStateSource()
        {
            _stateSource = stateSourceComponent as ISpacecraftStateSource;
            if (stateSourceComponent != null && _stateSource == null)
            {
                Debug.LogError(
                    $"{stateSourceComponent.name} must implement {nameof(ISpacecraftStateSource)}.",
                    stateSourceComponent);
            }
        }
    }
}
