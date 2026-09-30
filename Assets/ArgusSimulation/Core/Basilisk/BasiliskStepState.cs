using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // The state one Basilisk step reports at t_n: the StepResponse state fields. BasiliskEngine checks
    // run_id and sequence itself and reads pacing_overrun_count.
    internal readonly struct BasiliskStepState
    {
        private readonly BasiliskSensorSample[] _sensorSamples;

        public BasiliskStepState(
            long simulationTimeNanoseconds,
            BasiliskSpacecraftState spacecraft,
            BasiliskPlanetState earth,
            BasiliskPlanetState sun,
            double spacecraftShadowFactor,
            IReadOnlyList<BasiliskSensorSample> sensorSamples)
        {
            SimulationTimeNanoseconds = simulationTimeNanoseconds;
            Spacecraft = spacecraft;
            Earth = earth;
            Sun = sun;
            SpacecraftShadowFactor = spacecraftShadowFactor;
            _sensorSamples = new BasiliskSensorSample[sensorSamples == null ? 0 : sensorSamples.Count];
            for (int i = 0; i < _sensorSamples.Length; i++)
            {
                _sensorSamples[i] = sensorSamples[i];
            }
        }

        public long SimulationTimeNanoseconds { get; }
        public BasiliskSpacecraftState Spacecraft { get; }
        public BasiliskPlanetState Earth { get; }
        public BasiliskPlanetState Sun { get; }

        // EclipseMsg illuminationFactor (formerly shadowFactor): 1 is fully sunlit, 0 is umbra.
        public double SpacecraftShadowFactor { get; }

        public IReadOnlyList<BasiliskSensorSample> SensorSamples =>
            _sensorSamples ?? Array.Empty<BasiliskSensorSample>();

        public bool IsValid =>
            SimulationTimeNanoseconds >= 0 &&
            Spacecraft.IsValid &&
            Earth.IsValid &&
            Sun.IsValid &&
            SpacecraftShadowFactor >= 0.0 &&
            SpacecraftShadowFactor <= 1.0;
    }
}
