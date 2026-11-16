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
        // non-finite key, or a value the kind can never take, such as negative thrust), naming
        // the key, or null. Limits are Limit's.
        string FindCommandProblem(DeviceValues values);

        // Whether valid command values are exactly the idle values: the actuator applies nothing.
        bool IsIdle(DeviceValues values);

        // A device's limit_policy, from its valid parameters: what the gateway does with a command
        // past its limits (CORE-10, #46).
        ActuatorLimitPolicy LimitPolicyOf(DeviceValues parameters);

        // Valid command values held to the device's limits (its valid parameters). Within them, the
        // values themselves and excess null; past them, each value at its nearest limit, and excess
        // names every value that was past its limit. The kind owns the check, so a new kind brings
        // its own and the gateway doesn't change.
        DeviceValues Limit(DeviceValues parameters, DeviceValues values, out string excess);
    }
}
