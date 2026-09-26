using System;

namespace Argus.Simulation.Core
{
    // Development backend. Commands are recorded in each snapshot but do not yet perturb the orbit.
    // With an ephemeris provider, Earth-fixed state uses its J2000 -> ITRF93 transform and stepping
    // fails outside its coverage; without one, the simplified Earth rotation is used.
    public sealed class AnalyticSimulationEngine : ISimulationEngine
    {
        private readonly double _altitudeMeters;
        private readonly double _inclinationDegrees;
        private readonly double _raanDegrees;
        private readonly double _phaseDegrees;
        private readonly IEphemerisProvider _ephemeris;

        private CircularOrbitModel _orbit;
        private SimulationConfiguration _configuration;

        public AnalyticSimulationEngine(
            double altitudeMeters,
            double inclinationDegrees,
            double raanDegrees,
            double phaseDegrees,
            IEphemerisProvider ephemeris = null)
        {
            if (altitudeMeters <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(altitudeMeters));
            }

            _altitudeMeters = altitudeMeters;
            _inclinationDegrees = inclinationDegrees;
            _raanDegrees = raanDegrees;
            _phaseDegrees = phaseDegrees;
            _ephemeris = ephemeris;
        }

        public string BackendName => "analytic-circular-orbit";
        public bool IsInitialized => _orbit != null;
        public IEphemerisProvider Ephemeris => _ephemeris;

        public void Initialize(SimulationConfiguration configuration)
        {
            if (!configuration.IsValid)
            {
                throw new ArgumentException("A valid simulation configuration is required.", nameof(configuration));
            }

            // Simulation time t maps to the ephemeris at epoch + t, so the epochs must be identical.
            if (_ephemeris != null && _ephemeris.EpochUtc != configuration.EpochUtc)
            {
                throw new ArgumentException(
                    $"Ephemeris epoch {_ephemeris.EpochUtc:O} does not match run epoch {configuration.EpochUtc:O}.",
                    nameof(configuration));
            }

            _configuration = configuration;
            _orbit = new CircularOrbitModel(
                configuration.EpochUtc,
                _altitudeMeters,
                _inclinationDegrees,
                _raanDegrees,
                _phaseDegrees);
        }

        public void Reset()
        {
            if (!_configuration.IsValid)
            {
                _orbit = null;
                return;
            }

            Initialize(_configuration);
        }

        public bool TryStep(SimulationStepInput input, out SimulationSnapshot snapshot)
        {
            if (_orbit == null || !input.IsValid)
            {
                snapshot = default;
                return false;
            }

            SpacecraftState state;
            if (_ephemeris == null)
            {
                state = _orbit.Sample(input.Sequence, input.SimulationTimeSeconds);
            }
            else if (_ephemeris.TryGetSample(input.SimulationTimeSeconds, out EphemerisSample environment))
            {
                state = _orbit.Sample(input.Sequence, environment);
            }
            else
            {
                snapshot = default;
                return false;
            }

            snapshot = new SimulationSnapshot(
                _configuration.RunId,
                BackendName,
                state,
                input.Commands);
            return snapshot.IsValid;
        }
    }
}
