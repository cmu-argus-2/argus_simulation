using System;

namespace Argus.Simulation.Core
{
    public static class SolarGeometry
    {
        private const double TimeToleranceSeconds = 1e-9;

        // The state and ephemeris sample must describe the same instant; both are ITRF93 here.
        public static SunObservation Observe(SpacecraftState state, EphemerisSample environment)
        {
            if (Math.Abs(state.SimulationTimeSeconds - environment.SimulationTimeSeconds) > TimeToleranceSeconds)
            {
                throw new ArgumentException(
                    $"State time {state.SimulationTimeSeconds} s does not match ephemeris time " +
                    $"{environment.SimulationTimeSeconds} s.",
                    nameof(environment));
            }

            Vector3d sunItrf93 = environment.SunPositionItrf93Meters;
            Vector3d toSun = sunItrf93 - state.PositionEcefMeters;
            Vector3d directionItrf93 = toSun.Normalized();

            return new SunObservation(
                state.SimulationTimeSeconds,
                directionItrf93,
                state.BodyToEcef.Conjugate.Rotate(directionItrf93),
                toSun.Magnitude,
                EarthShadowModel.IlluminationFraction(state.PositionEcefMeters, sunItrf93));
        }
    }
}
