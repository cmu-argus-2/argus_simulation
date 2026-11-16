using Argus.Simulation.Core;

namespace Argus.Simulation.Tests
{
    // The analytic engine with a Sun: each state is tagged ITRF93 and carries an EnvironmentState, as
    // BasiliskEngine's states do, so a camera's render request has a Sun position (G3). Only the tag
    // and the environment are added; the orbit is still the analytic fixture's (D9), not a real
    // ITRF93 trajectory, and the Sun does not move.
    internal sealed class SunlitEngine : ISimulationEngine
    {
        public static readonly Vector3d SunPositionItrf93Meters = new Vector3d(1.47e11, 2.0e10, -6.0e10);

        private readonly AnalyticSimulationEngine _inner = new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0);

        public string BackendName => _inner.BackendName;
        public bool AppliesActuatorCommands => _inner.AppliesActuatorCommands;
        public bool IsInitialized => _inner.IsInitialized;
        public void Initialize(SimulationConfiguration configuration) => _inner.Initialize(configuration);

        public bool TryReset(out SimulationState state)
        {
            bool produced = _inner.TryReset(out state);
            if (produced)
            {
                state = WithSun(state);
            }

            return produced;
        }

        public bool TryStep(SimulationStepInput input, out SimulationState state)
        {
            bool produced = _inner.TryStep(input, out state);
            if (produced)
            {
                state = WithSun(state);
            }

            return produced;
        }

        private static SimulationState WithSun(SimulationState state)
        {
            SpacecraftState spacecraft = state.Spacecraft;
            return new SimulationState(
                state.RunId,
                state.DynamicsBackend,
                new SpacecraftState(
                    spacecraft.Sequence,
                    spacecraft.SimulationTimeSeconds,
                    spacecraft.TimestampUtc,
                    spacecraft.PositionEcefMeters,
                    spacecraft.VelocityEcefMetersPerSecond,
                    spacecraft.BodyToEcef,
                    spacecraft.AngularVelocityBodyRadiansPerSecond,
                    ReferenceFrame.Itrf93),
                state.AppliedCommands,
                new EnvironmentState(
                    SunPositionItrf93Meters,
                    new Quaterniond(0.0, 0.0, 0.0, 1.0),
                    new Vector3d(0.0, 0.0, 7.2921150e-5),
                    1.0),
                state.SensorMeasurements);
        }
    }
}
