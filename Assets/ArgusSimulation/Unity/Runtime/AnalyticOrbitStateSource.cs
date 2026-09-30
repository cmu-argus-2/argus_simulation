using System;
using System.Globalization;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // TODO(refactor): replace ISpacecraftStateSource with a snapshot source so one structure carries
    // every state (spacecraft, environment, sensor measurements, applied commands). TryGetState keeps
    // only snapshot.Spacecraft and drops the rest, which loses data once Basilisk is behind it. Decide
    // the common base contract for the state types in the same change, then rename this class (for
    // example AnalyticSnapshotSource), keeping its .meta GUID so Foundation.unity keeps the reference.
    // This is the roadmap's "Follower runner" item (docs/target-architecture.md §10).
    public sealed class AnalyticOrbitStateSource : MonoBehaviour, ISpacecraftStateSource
    {
        [SerializeField] private string epochUtc = "2025-01-15T00:00:00Z";
        [SerializeField, Min(100_000f)] private double altitudeMeters = 500_000.0;
        [SerializeField, Range(0f, 180f)] private double inclinationDegrees = 51.6;
        [SerializeField, Range(0f, 360f)] private double raanDegrees;
        [SerializeField, Range(0f, 360f)] private double phaseDegrees;

        private AnalyticSimulationEngine _engine;

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

        public bool TryGetState(long sequence, double simulationTimeSeconds, out SpacecraftState state)
        {
            if (_engine == null && !TryBuildEngine())
            {
                state = default;
                return false;
            }

            ActuatorCommandSet commands = ActuatorCommandSet.None(sequence, simulationTimeSeconds);
            SimulationStepInput input = new SimulationStepInput(
                sequence,
                simulationTimeSeconds,
                commands);
            if (!_engine.TryStep(input, out SimulationSnapshot snapshot))
            {
                state = default;
                return false;
            }

            state = snapshot.Spacecraft;
            return state.IsValid;
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
                "unity-preview",
                parsedEpoch,
                0.1));
            return true;
        }

        private void OnValidate()
        {
            _engine = null;
        }
    }
}
