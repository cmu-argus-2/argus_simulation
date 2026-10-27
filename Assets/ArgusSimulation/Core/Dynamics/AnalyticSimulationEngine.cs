using System;

namespace Argus.Simulation.Core
{
    // Development and test fixture (D9). Commands are recorded in each state but do not perturb the
    // orbit. It reports no EnvironmentState and never approximates SPICE data (D3). It ignores
    // RandomSeed, KernelSetId and Sensors.
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

        // The orbit every run starts from; the run recorder writes it to the manifest.
        public AnalyticOrbit Orbit => new AnalyticOrbit(_altitudeMeters, _inclinationDegrees, _raanDegrees, _phaseDegrees);
        public bool IsInitialized => _orbit != null;

        public void Initialize(SimulationConfiguration configuration)
        {
            if (!configuration.IsValid)
            {
                throw new ArgumentException("A valid simulation configuration is required.", nameof(configuration));
            }

            if (configuration.InitialOrbit.HasValue || configuration.Spacecraft.HasValue)
            {
                throw new ArgumentException(
                    "AnalyticSimulationEngine takes its circular orbit and attitude from its constructor; " +
                    "InitialOrbit and Spacecraft are Basilisk inputs.",
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

        public bool TryReset(out SimulationState state)
        {
            if (!_configuration.IsValid)
            {
                _orbit = null;
                state = default;
                return false;
            }

            Initialize(_configuration);
            return TrySample(0, 0.0, out state);
        }

        // Command k acts over [t_k, t_k+1) and is recorded in the state at t_k+1; it does not
        // perturb the orbit.
        public bool TryStep(SimulationStepInput input, out SimulationState state)
        {
            if (_orbit == null || !input.IsValid)
            {
                state = default;
                return false;
            }

            long sequence = input.Sequence + 1;
            state = new SimulationState(
                _configuration.RunId,
                BackendName,
                _orbit.Sample(sequence, sequence * _configuration.FixedStepSeconds),
                input.Commands);
            return state.IsValid;
        }

        // The state at any step, with no applied command: Unity's analytic state source and orbit
        // trail predict states directly instead of stepping a run.
        public bool TrySample(long sequence, double simulationTimeSeconds, out SimulationState state)
        {
            if (_orbit == null ||
                sequence < 0 ||
                simulationTimeSeconds < 0.0 ||
                double.IsNaN(simulationTimeSeconds) ||
                double.IsInfinity(simulationTimeSeconds))
            {
                state = default;
                return false;
            }

            state = new SimulationState(
                _configuration.RunId,
                BackendName,
                _orbit.Sample(sequence, simulationTimeSeconds),
                appliedCommands: null);
            return state.IsValid;
        }
    }
}
