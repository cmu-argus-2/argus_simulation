using System;

namespace Argus.Simulation.Core
{
    // Builds the Argus sensor for each sensor device of a run configuration through its registered
    // kind (DeviceKinds). Backend-modelled sensors publish what the engine measured (D10);
    // Argus-computed ones get their dependencies from the host or Unity (CORE-18).
    public static class SensorFactory
    {
        // Without a renderer, every camera exposure is Unavailable (D5).
        public static ISensor Create(DeviceConfiguration configuration) => Create(configuration, null);

        public static ISensor Create(DeviceConfiguration configuration, IImageRenderer imageRenderer)
        {
            if (!configuration.IsValid)
            {
                throw new ArgumentException(
                    $"A valid device configuration is required: {configuration.FindProblem() ?? "invalid definition"}.",
                    nameof(configuration));
            }

            IDeviceKind kind = configuration.KindDefinition;
            if (kind.Role != DeviceRole.Sensor)
            {
                throw new ArgumentException($"Device kind {kind.Name} is not a sensor.", nameof(configuration));
            }

            return kind.CreateSensor(configuration, imageRenderer);
        }
    }
}
