using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // One run configuration shared by Basilisk and Argus (G5). The optional members after
    // FixedStepSeconds are Basilisk inputs, except the devices Argus computes
    // (IDeviceKind.IsBackendModelled, CORE-18).
    public readonly struct SimulationConfiguration
    {
        private const int MaximumKernelSetIdLength = 64;

        private readonly DeviceConfiguration[] _devices;

        public SimulationConfiguration(
            string runId,
            DateTimeOffset epochUtc,
            double fixedStepSeconds,
            ClassicalOrbitElements? initialOrbit = null,
            int randomSeed = 0,
            string kernelSetId = null,
            SpacecraftConfiguration? spacecraft = null,
            IReadOnlyList<DeviceConfiguration> devices = null)
        {
            RunId = runId;
            EpochUtc = epochUtc.ToUniversalTime();
            FixedStepSeconds = fixedStepSeconds;
            InitialOrbit = initialOrbit;
            RandomSeed = randomSeed;
            KernelSetId = kernelSetId;
            Spacecraft = spacecraft;
            _devices = devices == null ? null : Copy(devices);
        }

        public string RunId { get; }
        public DateTimeOffset EpochUtc { get; }
        public double FixedStepSeconds { get; }
        public ClassicalOrbitElements? InitialOrbit { get; }
        public int RandomSeed { get; }

        // Names Argus.Basilisk/kernel_sets/<id>.json. Only the Basilisk service loads kernels.
        public string KernelSetId { get; }

        public SpacecraftConfiguration? Spacecraft { get; }
        // Every configured device, in configuration order (D11).
        public IReadOnlyList<DeviceConfiguration> Devices => _devices ?? Array.Empty<DeviceConfiguration>();

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(RunId) &&
            FixedStepSeconds > 0.0 &&
            !double.IsNaN(FixedStepSeconds) &&
            !double.IsInfinity(FixedStepSeconds) &&
            (!InitialOrbit.HasValue || InitialOrbit.Value.IsValid) &&
            (!Spacecraft.HasValue || Spacecraft.Value.IsValid) &&
            RandomSeed >= 0 &&
            (KernelSetId == null || IsKernelSetId(KernelSetId)) &&
            AreDevicesValid(Devices, FixedStepSeconds);

        private static DeviceConfiguration[] Copy(IReadOnlyList<DeviceConfiguration> devices)
        {
            DeviceConfiguration[] copy = new DeviceConfiguration[devices.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = devices[i];
            }

            return copy;
        }

        // Every sensor samples only at step states, so its period must be a whole number of steps:
        // otherwise an Argus sensor samples unevenly under a period it never keeps (CORE-18). Basilisk
        // also needs whole steps; BasiliskEngine checks those in exact nanoseconds. A sensor whose
        // frames carry truth may not be controller-visible, because the gateway forwards those frames
        // unchanged (D7, IDeviceKind.MayBeControllerVisible). Device IDs are unique across the run.
        private static bool AreDevicesValid(IReadOnlyList<DeviceConfiguration> devices, double fixedStepSeconds)
        {
            HashSet<string> deviceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (DeviceConfiguration device in devices)
            {
                if (!device.IsValid ||
                    !IsWholeNumberOfSteps(device.Definition.SamplePeriodSeconds, fixedStepSeconds) ||
                    (device.Definition.IsControllerVisible && !device.MayBeControllerVisible) ||
                    !deviceIds.Add(device.Definition.SensorId))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsWholeNumberOfSteps(double periodSeconds, double stepSeconds)
        {
            double steps = Math.Round(periodSeconds / stepSeconds);
            return steps >= 1.0 && SimulationTime.AreSame(steps * stepSeconds, periodSeconds);
        }

        // The service turns the ID into a file name, so only [a-z0-9][a-z0-9._-]* is accepted.
        private static bool IsKernelSetId(string value) =>
            value.Length <= MaximumKernelSetIdLength && IsLowercaseIdentifier(value);

        // [a-z0-9][a-z0-9._-]*: the kernel-set ID rule, which the run recorder also applies to the
        // run and device IDs it turns into directory names.
        internal static bool IsLowercaseIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                bool isLowerAlphanumeric = (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9');
                bool isSeparator = character == '.' || character == '_' || character == '-';
                if (!(isLowerAlphanumeric || (i > 0 && isSeparator)))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
