using System;
using System.Globalization;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    public sealed class AnalyticOrbitStateSource : MonoBehaviour, ISpacecraftStateSource
    {
        [SerializeField] private string epochUtc = "2025-01-15T00:00:00Z";
        [SerializeField, Min(100_000f)] private double altitudeMeters = 500_000.0;
        [SerializeField, Range(0f, 180f)] private double inclinationDegrees = 51.6;
        [SerializeField, Range(0f, 360f)] private double raanDegrees;
        [SerializeField, Range(0f, 360f)] private double phaseDegrees;

        private CircularOrbitModel _model;

        public double AltitudeMeters
        {
            get => altitudeMeters;
            set
            {
                altitudeMeters = Math.Max(100_000.0, Math.Min(2_000_000.0, value));
                _model = null;
            }
        }

        public double PhaseDegrees
        {
            get => phaseDegrees;
            set
            {
                phaseDegrees = ((value % 360.0) + 360.0) % 360.0;
                _model = null;
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
            if (_model == null && !TryBuildModel())
            {
                state = default;
                return false;
            }

            state = _model.Sample(sequence, simulationTimeSeconds);
            return true;
        }

        private bool TryBuildModel()
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

            _model = new CircularOrbitModel(
                parsedEpoch,
                altitudeMeters,
                inclinationDegrees,
                raanDegrees,
                phaseDegrees);
            return true;
        }

        private void OnValidate()
        {
            _model = null;
        }
    }
}
