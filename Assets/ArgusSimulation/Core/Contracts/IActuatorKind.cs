using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // What the gateway does with a command past a device's limits (CORE-10, #46). Append new
    // values only; never renumber.
    public enum ActuatorLimitPolicy
    {
        Unknown = 0,
        Clamp = 1,
        Reject = 2
    }

    // An actuator kind (D11, docs/device-model.md §2): it declares the values a command carries and
    // what the actuator runs when a step's command leaves it out. Its limits and limit policy are
    // its parameters. Actuators are backend-modelled (BSK-8, #21) and publish no frames.
    public interface IActuatorKind : IDeviceKind
    {
        // Every command key, all required.
        IReadOnlyList<DeviceKey> CommandKeys { get; }

        // The command values of a step whose command leaves the actuator out: no output.
        DeviceValues IdleValues { get; }

        // The first problem with one command's values (an unknown, missing, wrongly typed or
        // non-finite key), naming the key, or null. Limits are checked by the gateway (CORE-10).
        string FindCommandProblem(DeviceValues values);
    }
}
