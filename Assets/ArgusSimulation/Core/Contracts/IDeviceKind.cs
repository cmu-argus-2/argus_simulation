using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // Append new values only; never renumber.
    public enum DeviceRole
    {
        Unknown = 0,
        Sensor = 1,
        Actuator = 2
    }

    // One declared key of a kind's parameters, initial state or output.
    public readonly struct DeviceKey
    {
        public DeviceKey(string name, DeviceValueType type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }
        public DeviceValueType Type { get; }
    }

    // A generic device kind, registered by name in DeviceKinds (D11, docs/device-model.md §2). A kind
    // is a physics model, not a part: a specific part is a parameter set for it. Each kind declares
    // and validates its own keys, so adding one changes no contract, gateway or file format.
    public interface IDeviceKind
    {
        // The registered name, in snake_case: "imu", "light_sensor".
        string Name { get; }

        DeviceRole Role { get; }

        // Where the kind is computed (D10, CORE-18). Backend-modelled kinds keep their physics and
        // cadence in the dynamics backend: only they go to ConfigureRun and MapMeasurements, and
        // Argus never computes them. Argus-computed kinds never reach the backend.
        bool IsBackendModelled { get; }

        // Whether a controller may see the kind's frames (D7). The gateway forwards a controller-
        // visible frame unchanged, so a kind whose frames carry truth may not be visible, and
        // SimulationConfiguration.IsValid rejects such a device configured as visible.
        bool MayBeControllerVisible { get; }

        // Every parameter key, all required.
        IReadOnlyList<DeviceKey> ParameterKeys { get; }

        // Every initial-state key, all required: the kind's state at t = 0 (docs/device-model.md
        // §6). Empty for a kind with no state, such as every sensor kind so far.
        IReadOnlyList<DeviceKey> InitialStateKeys { get; }

        // The first problem with a device's parameters and initial state, naming the key, or null
        // when they are valid.
        string FindProblem(DeviceValues parameters, DeviceValues initialState);

        // Builds the sensor of a valid configuration of this kind. The renderer is the host's or
        // Unity's, for kinds that render (cameras); null means none, and other kinds ignore it.
        ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer);
    }

    // A sensor kind the backend models: it reads the backend's output map into its typed measurement.
    public interface IBackendSensorKind : IDeviceKind
    {
        IReadOnlyList<DeviceKey> OutputKeys { get; }

        // The typed measurement of a non-empty output map (an empty map is Unavailable). Throws
        // ArgumentException naming the key when the map is not exactly the declared keys and types.
        object ReadOutput(DeviceValues output);
    }
}
