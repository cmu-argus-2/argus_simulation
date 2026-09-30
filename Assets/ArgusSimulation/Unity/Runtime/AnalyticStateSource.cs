using System;
using System.Globalization;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // Development state source: the analytic fixture (D9) inside Unity, stepped by SimulationRunner's
    // clock. It publishes whole SimulationStates, which carry no environment or sensor measurements
    // because the fixture never approximates SPICE or Basilisk data. Basilisk runs use
    // StateStreamClient instead (G2). Each ResetRun starts a new run ID, so the states and the sensor
    // frames of one run share it (D6).
    public sealed class AnalyticStateSource : MonoBehaviour, IStepDrivenStateSource
    {
        [SerializeField] private string epochUtc = "2025-01-15T00:00:00Z";
        [SerializeField, Min(100_000f)] private double altitudeMeters = 500_000.0;
        [SerializeField, Range(0f, 180f)] private double inclinationDegrees = 51.6;
        [SerializeField, Range(0f, 360f)] private double raanDegrees;
        [SerializeField, Range(0f, 360f)] private double phaseDegrees;

        private AnalyticSimulationEngine _engine;
        private string _runId;

        public event Action<SimulationState> StateProduced;

        public string RunId => _runId ?? (_runId = NewRunId());

        public double AltitudeMeters
        {
            get => altitudeMeters;
            set
            {
                altitudeMeters = Math.Max(100_000.0, Math.Min(2_000_000.0, value));
                _engine = null;
            }
        }

        public double PhaseDegrees
        {
            get => phaseDegrees;
            set
            {
                phaseDegrees = ((value % 360.0) + 360.0) % 360.0;
                _engine = null;
            }
        }

        public double EstimatedPeriodSeconds
        {
            get
            {
                double radius = CircularOrbitModel.EarthEquatorialRadiusMeters + altitudeMeters;
                return 2.0 * Math.PI * Math.Sqrt(
                    radius * radius * radius / CircularOrbitModel.EarthGravitationalParameter);
            }
        }

        public bool TryStep(long sequence, double simulationTimeSeconds)
        {
            if (!TryPredictState(sequence, simulationTimeSeconds, out SimulationState state))
            {
                return false;
            }

            StateProduced?.Invoke(state);
            return true;
        }

        public void ResetRun()
        {
            _runId = NewRunId();
            _engine = null;
        }

        // The analytic state at any step, without publishing it. OrbitTrailRenderer draws the
        // predicted orbit from it in development runs.
        public bool TryPredictState(long sequence, double simulationTimeSeconds, out SimulationState state)
        {
            if (_engine == null && !TryBuildEngine())
            {
                state = default;
                return false;
            }

            SimulationStepInput input = new SimulationStepInput(
                sequence,
                simulationTimeSeconds,
                ActuatorCommandSet.None(sequence, simulationTimeSeconds));
            return _engine.TryStep(input, out state) && state.IsValid;
        }

        private bool TryBuildEngine()
        {
            if (!DateTimeOffset.TryParse(
                    epochUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsedEpoch))
            {
                Debug.LogError($"Invalid UTC epoch: {epochUtc}", this);
                return false;
            }

            _engine = new AnalyticSimulationEngine(
                altitudeMeters,
                inclinationDegrees,
                raanDegrees,
                phaseDegrees);
            _engine.Initialize(new SimulationConfiguration(
                RunId,
                parsedEpoch,
                0.1));
            return true;
        }

        private static string NewRunId() => "unity-" + Guid.NewGuid().ToString("N");

        private void OnValidate()
        {
            _engine = null;
        }
    }
}
