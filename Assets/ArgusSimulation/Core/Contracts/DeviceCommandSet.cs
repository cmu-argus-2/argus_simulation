using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // A controller's command for one step: per-device values for configured actuators (D11,
    // docs/device-model.md §6). It is complete: an actuator it leaves out uses its kind's idle
    // values, so the empty set (None) means no output.
    public readonly struct DeviceCommandSet
    {
        private readonly DeviceCommand[] _commands;

        public DeviceCommandSet(long sequence, double applyAtSimulationTimeSeconds, IReadOnlyList<DeviceCommand> commands = null)
        {
            Sequence = sequence;
            ApplyAtSimulationTimeSeconds = applyAtSimulationTimeSeconds;
            _commands = commands == null ? null : Copy(commands);
        }

        public long Sequence { get; }
        public double ApplyAtSimulationTimeSeconds { get; }

        // In the order the controller sent them.
        public IReadOnlyList<DeviceCommand> Commands => _commands ?? Array.Empty<DeviceCommand>();

        // A well-formed set: a valid sequence and time, and each device at most once with a
        // non-blank ID and finite values. Whether each device is a configured actuator, and its
        // values its kind's, is the gateway's check against the run.
        public bool IsValid => Sequence >= 0 &&
            !double.IsNaN(ApplyAtSimulationTimeSeconds) &&
            !double.IsInfinity(ApplyAtSimulationTimeSeconds) &&
            ApplyAtSimulationTimeSeconds >= 0.0 &&
            FindProblem() == null;

        // The first problem with the commands themselves, naming the device, or null.
        public string FindProblem()
        {
            HashSet<string> deviceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (DeviceCommand command in Commands)
            {
                if (string.IsNullOrWhiteSpace(command.DeviceId))
                {
                    return "a command has a blank device ID";
                }

                if (!deviceIds.Add(command.DeviceId))
                {
                    return $"device '{command.DeviceId}' is commanded twice";
                }

                foreach (KeyValuePair<string, DeviceValue> value in command.Values)
                {
                    if (!value.Value.IsFinite)
                    {
                        return $"device '{command.DeviceId}' value \"{value.Key}\" is not finite";
                    }
                }
            }

            return null;
        }

        public static DeviceCommandSet None(long sequence, double simulationTimeSeconds) =>
            new DeviceCommandSet(sequence, simulationTimeSeconds);

        private static DeviceCommand[] Copy(IReadOnlyList<DeviceCommand> commands)
        {
            DeviceCommand[] copy = new DeviceCommand[commands.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = commands[i];
            }

            return copy;
        }
    }
}
