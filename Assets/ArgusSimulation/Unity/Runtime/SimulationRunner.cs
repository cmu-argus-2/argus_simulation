using System;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // Publishes the headless host's SimulationStates to Unity consumers (G2). The host owns the run:
    // its run configuration is the only one, and in Basilisk runs Basilisk owns simulation time
    // (D2). States arrive through the scene's ISimulationStateSource (StateStreamClient) and are
    // published as delivered, never stepped, re-timed or paused; Unity has no simulation of its own.
    // Presentation (CesiumSpacecraftPoseDriver) interpolates between them without changing them.
    // When the source is also an ISensorOutputSource, LastOutput keeps the SensorOutputSet paired
    // with LastState, so the dashboard's sensors always describe the state it shows.
    public sealed class SimulationRunner : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour stateSourceComponent;

        // SimulationRate averages over this many arrivals, since one is stamped at frame time.
        private const int RateWindow = 5;

        private readonly double[] _arrivalSeconds = new double[RateWindow];
        private readonly double[] _arrivalSimulationSeconds = new double[RateWindow];
        private ISimulationStateSource _stateSource;
        private ISensorOutputSource _outputSource;
        private bool _isSubscribed;
        private int _arrivals;

        public event Action<SimulationState> StateProduced;

        // Raised before the first state of a new run (run_generation): clear interpolation and history.
        public event Action HistoryReset;

        public bool HasState { get; private set; }
        public SimulationState LastState { get; private set; }
        // The sensor output set of LastState, or null before it arrives, for a state source that sends
        // none, and after HistoryReset.
        public SensorOutputSet LastOutput { get; private set; }

        public bool HasOutputSource => _outputSource != null;

        public double SimulationTimeSeconds => HasState ? LastState.Spacecraft.SimulationTimeSeconds : 0.0;

        // Simulated seconds per wall-clock second over the last few states, or 0 before two: how fast
        // the host's run advances (1 when Basilisk paces it in real time).
        public double SimulationRate { get; private set; }

        public ISimulationStateSource StateSource => _stateSource;

        private void Awake()
        {
            ResolveStateSource();
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        public void Configure(MonoBehaviour source)
        {
            stateSourceComponent = source;
            ResolveStateSource();
        }

        private void HandleState(SimulationState state)
        {
            if (!state.IsValid)
            {
                Debug.LogError($"{name} ignored an invalid simulation state.", this);
                return;
            }

            int slot = _arrivals % RateWindow;
            _arrivalSeconds[slot] = Time.realtimeSinceStartupAsDouble;
            _arrivalSimulationSeconds[slot] = state.Spacecraft.SimulationTimeSeconds;
            _arrivals++;
            if (_arrivals > 1)
            {
                int oldest = _arrivals > RateWindow ? _arrivals % RateWindow : 0;
                double wall = _arrivalSeconds[slot] - _arrivalSeconds[oldest];
                if (wall > 0.0)
                {
                    SimulationRate = (_arrivalSimulationSeconds[slot] - _arrivalSimulationSeconds[oldest]) / wall;
                }
            }

            LastState = state;
            HasState = true;
            LastOutput = null;
            StateProduced?.Invoke(state);
        }

        // A set arrives right after its state; one whose state was rejected, or that belongs to
        // another state, is dropped so LastOutput never describes a state other than LastState.
        private void HandleOutput(SensorOutputSet output)
        {
            if (output == null || !HasState ||
                output.RunId != LastState.RunId ||
                output.SpacecraftSequence != LastState.Spacecraft.Sequence)
            {
                return;
            }

            LastOutput = output;
        }

        private void HandleHistoryReset()
        {
            HasState = false;
            LastState = default;
            LastOutput = null;
            SimulationRate = 0.0;
            _arrivals = 0;
            HistoryReset?.Invoke();
        }

        private void ResolveStateSource()
        {
            Unsubscribe();
            _stateSource = stateSourceComponent as ISimulationStateSource;
            _outputSource = _stateSource as ISensorOutputSource;
            LastOutput = null;
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
            _stateSource.HistoryReset += HandleHistoryReset;
            if (_outputSource != null)
            {
                _outputSource.OutputProduced += HandleOutput;
            }
            _isSubscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_isSubscribed)
            {
                return;
            }

            _stateSource.StateProduced -= HandleState;
            _stateSource.HistoryReset -= HandleHistoryReset;
            if (_outputSource != null)
            {
                _outputSource.OutputProduced -= HandleOutput;
            }
            _isSubscribed = false;
        }
    }
}
