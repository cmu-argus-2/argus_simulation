using System;
using System.Globalization;
using System.IO;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    public sealed class AnalyticOrbitStateSource : MonoBehaviour,
        ISpacecraftStateSource,
        IAttitudeOverrideTarget
    {
        [SerializeField] private string epochUtc = "2025-01-15T00:00:00Z";
        [SerializeField, Min(100_000f)] private double altitudeMeters = 500_000.0;
        [SerializeField, Range(0f, 180f)] private double inclinationDegrees = 51.6;
        [SerializeField, Range(0f, 360f)] private double raanDegrees;
        [SerializeField, Range(0f, 360f)] private double phaseDegrees;

        [Tooltip("Query a live SPICE runtime for Earth orientation and Sun geometry. Off uses simplified Earth rotation and no Sun environment.")]
        [SerializeField] private bool useSpiceEphemeris = true;

        [Tooltip("Project-relative path to the persistent SPICE runtime launcher. ARGUS_SPICE_RUNTIME_LAUNCHER overrides this value.")]
        [SerializeField] private string spiceRuntimeLauncher = "Argus.Spice/run_runtime.sh";

        private AnalyticSimulationEngine _engine;
        private IEphemerisProvider _ephemeris;
        private bool _ephemerisStartFailed;
        private Quaterniond _attitudeOverrideBody = Quaterniond.Identity;

        public IEphemerisProvider Ephemeris => _ephemeris;

        public bool UseSpiceEphemeris
        {
            get => useSpiceEphemeris;
            set
            {
                useSpiceEphemeris = value;
                OnValidate();
            }
        }

        public string SpiceRuntimeLauncher
        {
            get => spiceRuntimeLauncher;
            set
            {
                spiceRuntimeLauncher = value;
                OnValidate();
            }
        }

        public Quaterniond AttitudeOverrideBody => _attitudeOverrideBody;

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

        public bool TryGetSunObservation(SpacecraftState state, out SunObservation observation)
        {
            if (_ephemeris == null ||
                !_ephemeris.TryGetSample(state.SimulationTimeSeconds, out EphemerisSample environment))
            {
                observation = default;
                return false;
            }

            observation = SolarGeometry.Observe(state, environment);
            return true;
        }

        public bool TryApplyAttitudeOverride(AttitudeOverrideCommand command)
        {
            if (!command.IsValid)
            {
                return false;
            }

            _attitudeOverrideBody = command.Operation == AttitudeOverrideOperation.Clear
                ? Quaterniond.Identity
                : (_attitudeOverrideBody * command.BodyFrameDelta).Normalized();
            _engine = null;
            return true;
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

            if (useSpiceEphemeris && _ephemeris == null && !TryStartEphemeris(parsedEpoch))
            {
                return false;
            }

            AnalyticSimulationEngine engine = new AnalyticSimulationEngine(
                altitudeMeters,
                inclinationDegrees,
                raanDegrees,
                phaseDegrees,
                useSpiceEphemeris ? _ephemeris : null,
                _attitudeOverrideBody);
            try
            {
                engine.Initialize(new SimulationConfiguration(
                    "unity-preview",
                    parsedEpoch,
                    0.1));
            }
            catch (ArgumentException error)
            {
                Debug.LogError(error.Message, this);
                return false;
            }

            _engine = engine;
            return true;
        }

        private bool TryStartEphemeris(DateTimeOffset epoch)
        {
            if (_ephemerisStartFailed)
            {
                return false;
            }

            string configured = Environment.GetEnvironmentVariable("ARGUS_SPICE_RUNTIME_LAUNCHER");
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = spiceRuntimeLauncher;
            }

            string path = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", configured));
            try
            {
                _ephemeris = new SpiceRuntimeEphemerisProvider(epoch, path);
                return true;
            }
            catch (Exception error)
            {
                _ephemerisStartFailed = true;
                Debug.LogError($"Could not start SPICE runtime '{path}': {error.Message}", this);
                return false;
            }
        }

        private void OnValidate()
        {
            DisposeEphemeris();
            _engine = null;
            _ephemerisStartFailed = false;
        }

        private void OnDestroy() => DisposeEphemeris();

        private void DisposeEphemeris()
        {
            if (_ephemeris is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _ephemeris = null;
        }
    }
}
