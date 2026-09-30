using System;

namespace Argus.Simulation.Core
{
    // A light sensor is a Basilisk coarseSunSensor whose output is irradiance on its aperture.
    // Comments name the attribute each field sets; noise and bias are given after scaling and the
    // service divides them by the Basilisk scaleFactor.
    public readonly struct LightSensorProfile
    {
        public LightSensorProfile(
            double fieldOfViewHalfAngleRadians,
            double scaleFactor,
            double noiseStdWattsPerSquareMeter,
            double biasWattsPerSquareMeter,
            double saturationWattsPerSquareMeter)
        {
            FieldOfViewHalfAngleRadians = fieldOfViewHalfAngleRadians;
            ScaleFactor = scaleFactor;
            NoiseStdWattsPerSquareMeter = noiseStdWattsPerSquareMeter;
            BiasWattsPerSquareMeter = biasWattsPerSquareMeter;
            SaturationWattsPerSquareMeter = saturationWattsPerSquareMeter;
        }

        // fov, also passed to albedo's instrument configuration. At most pi/2: Basilisk gates on
        // cos(angle) >= cos(fov) without clamping, so a wider field adds negative irradiance from
        // the Sun or Earth behind the aperture. pi/2 already covers the whole hemisphere.
        public double FieldOfViewHalfAngleRadians { get; }

        // Dimensionless gain g; Basilisk scaleFactor = g x 1361 W/m^2.
        public double ScaleFactor { get; }

        // senNoiseStd = noise / scaleFactor, with AMatrix = 0.
        public double NoiseStdWattsPerSquareMeter { get; }

        // senBias = bias / scaleFactor.
        public double BiasWattsPerSquareMeter { get; }

        // maxOutput; minOutput is 0.
        public double SaturationWattsPerSquareMeter { get; }

        public bool IsValid =>
            IsFinite(FieldOfViewHalfAngleRadians) &&
            FieldOfViewHalfAngleRadians > 0.0 &&
            FieldOfViewHalfAngleRadians <= Math.PI / 2.0 &&
            IsFinite(ScaleFactor) &&
            ScaleFactor > 0.0 &&
            IsFinite(NoiseStdWattsPerSquareMeter) &&
            NoiseStdWattsPerSquareMeter >= 0.0 &&
            IsFinite(BiasWattsPerSquareMeter) &&
            IsFinite(SaturationWattsPerSquareMeter) &&
            SaturationWattsPerSquareMeter > 0.0;

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
