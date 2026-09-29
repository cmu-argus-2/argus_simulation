namespace Argus.Simulation.Core
{
    // Basilisk coarseSunSensor CSSRawDataMsgPayload.OutputData, with the service setting
    // scaleFactor = gain x 1361 W/m^2 (IAU 2015 B3) so the output is irradiance.
    public readonly struct LightSensorMeasurement
    {
        public LightSensorMeasurement(double irradianceWattsPerSquareMeter)
        {
            IrradianceWattsPerSquareMeter = irradianceWattsPerSquareMeter;
        }

        // On the sensor aperture: direct Sun (cosine law, field of view, (1 AU / r)^2, eclipse) plus
        // Earth albedo, after scale, noise and bias, then clipped to saturation.
        public double IrradianceWattsPerSquareMeter { get; }

        public bool IsValid =>
            !double.IsNaN(IrradianceWattsPerSquareMeter) &&
            !double.IsInfinity(IrradianceWattsPerSquareMeter) &&
            IrradianceWattsPerSquareMeter >= 0.0;
    }
}
