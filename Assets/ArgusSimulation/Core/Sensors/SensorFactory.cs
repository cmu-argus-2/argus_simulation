using System;

namespace Argus.Simulation.Core
{
    // Builds the Argus sensor for each backend-modelled sensor of a run configuration.
    public static class SensorFactory
    {
        public static ISensor Create(SensorConfiguration configuration)
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
                default:
                    throw new ArgumentException(
                        $"Unsupported sensor kind {configuration.Kind}.",
                        nameof(configuration));
            }
        }
    }
}
