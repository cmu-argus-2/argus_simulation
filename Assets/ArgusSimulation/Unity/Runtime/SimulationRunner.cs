using System;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // Publishes whole SimulationStates from its state source to Unity consumers. For a step-driven
    // source (the analytic fixture in development runs) it also owns the clock: fixed steps from
    // Unity frame time x time scale, which pause, StepOnce and TimeScale control.
    // TODO(G2): for Basilisk runs it follows StateStreamClient, whose states arrive on Basilisk's
    // clock (D2), so pause, StepOnce and TimeScale do not apply there.
    public sealed class SimulationRunner : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour stateSourceComponent;
        [SerializeField, Min(0.001f)] private double fixedStepSeconds = 0.1;
        [SerializeField, Min(0.0f)] private double timeScale = 1.0;
        [SerializeField] private bool runOnStart = true;
        [SerializeField, Min(1)] private int maximumStepsPerFrame = 100;

        private ISimulationStateSource _stateSource;
        private IStepDrivenStateSource _stepDrivenSource;
        private bool _isSubscribed;
        private bool _stateAccepted;
        private double _accumulatorSeconds;
        private double _simulationTimeSeconds;
        private long _sequence;

        public event Action<SimulationState> StateProduced;
        public event Action SimulationReset;

        public bool IsRunning { get; set; }
        public double SimulationTimeSeconds => _simulationTimeSeconds;
        public double FixedStepSeconds => fixedStepSeconds;
        public bool HasState { get; private set; }
        public SimulationState LastState { get; private set; }
        public ISimulationStateSource StateSource => _stateSource;
        public double TimeScale
        {
            get => timeScale;
            set => timeScale = Math.Max(0.0, value);
        }

        private void Awake()
        {
            ResolveStateSource();
            IsRunning = runOnStart;
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Update()
        {
            if (!IsRunning || _stepDrivenSource == null)
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

        // Advances a step-driven source by one fixed step; false for a follower source.
        public bool StepOnce()
        {
            if (_stateSource == null)
            {
                ResolveStateSource();
            }

            if (_stepDrivenSource == null)
            {
                return false;
            }

            _stateAccepted = false;
            if (!_stepDrivenSource.TryStep(_sequence, _simulationTimeSeconds) || !_stateAccepted)
            {
                return false;
            }

            _sequence++;
            _simulationTimeSeconds = _sequence * fixedStepSeconds;
            return true;
        }

        public void ResetSimulation()
        {
            _stepDrivenSource?.ResetRun();
            _accumulatorSeconds = 0.0;
            _simulationTimeSeconds = 0.0;
            _sequence = 0;
            HasState = false;
            LastState = default;
            SimulationReset?.Invoke();
        }

        private void HandleState(SimulationState state)
        {
            if (!state.IsValid)
            {
                Debug.LogError($"{name} ignored an invalid simulation state.", this);
                return;
            }

            LastState = state;
            HasState = true;
            _stateAccepted = true;
            StateProduced?.Invoke(state);
        }

        private void ResolveStateSource()
        {
            Unsubscribe();
            _stateSource = stateSourceComponent as ISimulationStateSource;
            _stepDrivenSource = _stateSource as IStepDrivenStateSource;
            if (stateSourceComponent != null && _stateSource == null)
            {
                Debug.LogError(
                    $"{stateSourceComponent.name} must implement {nameof(ISimulationStateSource)}.",
                    stateSourceComponent);
            }

            if (isActiveAndEnabled)
            {
                Subscribe();
            }
        }

        private void Subscribe()
        {
            if (_isSubscribed || _stateSource == null)
            {
                return;
            }

            _stateSource.StateProduced += HandleState;
            _isSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_isSubscribed)
            {
                return;
            }

            _stateSource.StateProduced -= HandleState;
            _isSubscribed = false;
        }
    }
}
