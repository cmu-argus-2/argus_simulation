using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // One run configuration shared by Basilisk and Argus (G5). The optional members after
    // FixedStepSeconds are Basilisk inputs, except the sensors Argus computes
    // (SensorConfiguration.IsBackendModelled, CORE-18).
    public readonly struct SimulationConfiguration
    {
        private const int MaximumKernelSetIdLength = 64;

        private readonly SensorConfiguration[] _sensors;

        public SimulationConfiguration(
            string runId,
            DateTimeOffset epochUtc,
            double fixedStepSeconds,
            ClassicalOrbitElements? initialOrbit = null,
            int randomSeed = 0,
            string kernelSetId = null,
            SpacecraftConfiguration? spacecraft = null,
            IReadOnlyList<SensorConfiguration> sensors = null)
        {
            RunId = runId;
            EpochUtc = epochUtc.ToUniversalTime();
            FixedStepSeconds = fixedStepSeconds;
            InitialOrbit = initialOrbit;
            RandomSeed = randomSeed;
            KernelSetId = kernelSetId;
            Spacecraft = spacecraft;
            _sensors = sensors == null ? null : Copy(sensors);
        }

        public string RunId { get; }
        public DateTimeOffset EpochUtc { get; }
        public double FixedStepSeconds { get; }
        public ClassicalOrbitElements? InitialOrbit { get; }
        public int RandomSeed { get; }

        // Names Argus.Basilisk/kernel_sets/<id>.json. Only the Basilisk service loads kernels.
        public string KernelSetId { get; }

        public SpacecraftConfiguration? Spacecraft { get; }
        public IReadOnlyList<SensorConfiguration> Sensors => _sensors ?? Array.Empty<SensorConfiguration>();

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(RunId) &&
            FixedStepSeconds > 0.0 &&
            !double.IsNaN(FixedStepSeconds) &&
            !double.IsInfinity(FixedStepSeconds) &&
            (!InitialOrbit.HasValue || InitialOrbit.Value.IsValid) &&
            (!Spacecraft.HasValue || Spacecraft.Value.IsValid) &&
            RandomSeed >= 0 &&
            (KernelSetId == null || IsKernelSetId(KernelSetId)) &&
            AreSensorsValid(Sensors, FixedStepSeconds);

        private static SensorConfiguration[] Copy(IReadOnlyList<SensorConfiguration> sensors)
        {
            SensorConfiguration[] copy = new SensorConfiguration[sensors.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = sensors[i];
            }

            return copy;
        }

        // Every sensor samples only at step states, so its period must be a whole number of steps:
        // otherwise an Argus sensor samples unevenly under a period it never keeps (CORE-18). Basilisk
        // also needs whole steps; BasiliskEngine checks those in exact nanoseconds.
        private static bool AreSensorsValid(IReadOnlyList<SensorConfiguration> sensors, double fixedStepSeconds)
        {
            HashSet<string> sensorIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (SensorConfiguration sensor in sensors)
            {
                if (!sensor.IsValid ||
                    !IsWholeNumberOfSteps(sensor.Definition.SamplePeriodSeconds, fixedStepSeconds) ||
                    !sensorIds.Add(sensor.Definition.SensorId))
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
        // run and sensor IDs it turns into directory names.
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
