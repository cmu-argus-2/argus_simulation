using System;

namespace Argus.Simulation.Core
{
    // Development backend. Commands are recorded in each snapshot but do not yet perturb the orbit.
    public sealed class AnalyticSimulationEngine : ISimulationEngine
    {
        private readonly double _altitudeMeters;
        private readonly double _inclinationDegrees;
        private readonly double _raanDegrees;
        private readonly double _phaseDegrees;

        private CircularOrbitModel _orbit;
        private SimulationConfiguration _configuration;

        public AnalyticSimulationEngine(
            double altitudeMeters,
            double inclinationDegrees,
            double raanDegrees,
            double phaseDegrees)
        {
            if (altitudeMeters <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(altitudeMeters));
            }

            _altitudeMeters = altitudeMeters;
            _inclinationDegrees = inclinationDegrees;
            _raanDegrees = raanDegrees;
            _phaseDegrees = phaseDegrees;
        }

        public string BackendName => "analytic-circular-orbit";
        public bool IsInitialized => _orbit != null;

        public void Initialize(SimulationConfiguration configuration)
        {
            if (!configuration.IsValid)
            {
                throw new ArgumentException("A valid simulation configuration is required.", nameof(configuration));
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

            SpacecraftState state = _orbit.Sample(input.Sequence, input.SimulationTimeSeconds);
            snapshot = new SimulationSnapshot(
                _configuration.RunId,
                BackendName,
                state,
                input.Commands);
            return snapshot.IsValid;
        }
    }
}
