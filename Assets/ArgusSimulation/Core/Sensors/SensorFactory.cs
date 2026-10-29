using System;

namespace Argus.Simulation.Core
{
    // Builds the Argus sensor for each sensor of a run configuration. Backend-modelled sensors publish
    // what the engine measured (D10); Argus-computed ones get their dependencies from the host or Unity
    // (CORE-18).
    public static class SensorFactory
    {
        // Without a renderer, every camera exposure is Unavailable (D5).
        public static ISensor Create(SensorConfiguration configuration) => Create(configuration, null);

        public static ISensor Create(SensorConfiguration configuration, IImageRenderer imageRenderer)
        {
            if (!configuration.IsValid)
            {
                throw new ArgumentException("A valid sensor configuration is required.", nameof(configuration));
            }

            switch (configuration.Kind)
            {
                case SensorKind.Imu:
                    return new ImuSensor(configuration);
                case SensorKind.Magnetometer:
                    return new MagnetometerSensor(configuration);
                case SensorKind.LightSensor:
                    return new LightSensor(configuration);
                case SensorKind.Camera:
                    return new ArducamImx708CameraModel(configuration, imageRenderer);
                default:
                    throw new ArgumentException(
                        $"Unsupported sensor kind {configuration.Kind}.",
                        nameof(configuration));
            }
        }
    }
}
