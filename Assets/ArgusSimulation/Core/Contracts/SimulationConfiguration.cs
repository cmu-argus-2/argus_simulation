using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // One run configuration shared by Basilisk and Argus (G5). The optional members after
    // FixedStepSeconds are Basilisk inputs, except the devices Argus computes
    // (IDeviceKind.IsBackendModelled, CORE-18) and the gateway settings HeartbeatTimeoutWallSeconds
    // and PhysicalActuators (CORE-9), which the service validates and otherwise ignores.
    public readonly struct SimulationConfiguration
    {
        private const int MaximumKernelSetIdLength = 64;

        private readonly DeviceConfiguration[] _devices;
        private readonly DeviceConfiguration[] _backendDevices;
        private readonly DeviceConfiguration[] _backendSensors;

        public SimulationConfiguration(
            string runId,
            DateTimeOffset epochUtc,
            double fixedStepSeconds,
            ClassicalOrbitElements? initialOrbit = null,
            int randomSeed = 0,
            string kernelSetId = null,
            SpacecraftConfiguration? spacecraft = null,
            IReadOnlyList<DeviceConfiguration> devices = null,
            double heartbeatTimeoutWallSeconds = 0.0,
            PhysicalActuatorSetting physicalActuators = default)
        {
            RunId = runId;
            EpochUtc = epochUtc.ToUniversalTime();
            FixedStepSeconds = fixedStepSeconds;
            InitialOrbit = initialOrbit;
            RandomSeed = randomSeed;
            KernelSetId = kernelSetId;
            Spacecraft = spacecraft;
            _devices = devices == null ? null : Copy(devices);
            _backendDevices = Where(_devices, device => device.IsBackendModelled);
            _backendSensors = Where(_backendDevices, device => device.IsBackendSensor);
            HeartbeatTimeoutWallSeconds = heartbeatTimeoutWallSeconds;
            PhysicalActuators = physicalActuators;
        }

        // The gateway's heartbeat timeout for the controller holding actuator authority (CORE-9); 0
        // turns it off. Wall-clock seconds, not simulation time. A gateway setting: the Basilisk service
        // validates it and otherwise ignores it.
        public double HeartbeatTimeoutWallSeconds { get; }

        // The physical-actuator inhibit (CORE-9), for HIL-1 to enforce; inhibited by default.
        public PhysicalActuatorSetting PhysicalActuators { get; }

        // This configuration with seed in place of RandomSeed and every other member kept, so the
        // gateway's Reset(seed, scenario) never drops a member added later.
        public SimulationConfiguration WithRandomSeed(int seed) =>
            new SimulationConfiguration(
                RunId,
                EpochUtc,
                FixedStepSeconds,
                InitialOrbit,
                seed,
                KernelSetId,
                Spacecraft,
                _devices,
                HeartbeatTimeoutWallSeconds,
                PhysicalActuators);

        // A run that lifts the physical-actuator inhibit must fail safe within this many seconds of
        // wall-clock time after its controller falls silent (CORE-9).
        // TODO(hardware): a placeholder until HIL-1 (#66) confirms it against the bench procedure.
        public const double MaximumHeartbeatTimeoutWithPhysicalActuatorsWallSeconds = 60.0;

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

        // The devices the dynamics backend models, in configuration order: what ConfigureRun sends
        // (D10, CORE-18). Worked out once, when the configuration is built (CORE-22).
        public IReadOnlyList<DeviceConfiguration> BackendDevices => _backendDevices ?? Array.Empty<DeviceConfiguration>();

        // The backend devices that are sensors the backend samples (DeviceConfiguration.IsBackendSensor),
        // in configuration order: the ones whose measurements come back, on the steps their sample
        // periods divide. A backend-modelled actuator (CON-2) is in BackendDevices only.
        public IReadOnlyList<DeviceConfiguration> BackendSensors => _backendSensors ?? Array.Empty<DeviceConfiguration>();

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(RunId) &&
            FixedStepSeconds > 0.0 &&
            !double.IsNaN(FixedStepSeconds) &&
            !double.IsInfinity(FixedStepSeconds) &&
            (!InitialOrbit.HasValue || InitialOrbit.Value.IsValid) &&
            (!Spacecraft.HasValue || Spacecraft.Value.IsValid) &&
            RandomSeed >= 0 &&
            (KernelSetId == null || IsKernelSetId(KernelSetId)) &&
            AreDevicesValid(Devices, FixedStepSeconds) &&
            IsControllerSettingValid(HeartbeatTimeoutWallSeconds, PhysicalActuators);

        // The heartbeat timeout is finite and >= 0 (0 is off). Lifting the physical-actuator inhibit
        // needs a test procedure and a heartbeat failsafe of at most 60 s (CORE-9).
        private static bool IsControllerSettingValid(double heartbeatTimeoutWallSeconds, PhysicalActuatorSetting physicalActuators)
        {
            if (double.IsNaN(heartbeatTimeoutWallSeconds) || double.IsInfinity(heartbeatTimeoutWallSeconds) || heartbeatTimeoutWallSeconds < 0.0)
            {
                return false;
            }

            if (!physicalActuators.IsValid)
            {
                return false;
            }

            return !physicalActuators.Enabled ||
                (heartbeatTimeoutWallSeconds > 0.0 && heartbeatTimeoutWallSeconds <= MaximumHeartbeatTimeoutWithPhysicalActuatorsWallSeconds);
        }

        private static DeviceConfiguration[] Where(DeviceConfiguration[] devices, Func<DeviceConfiguration, bool> keep)
        {
            if (devices == null)
            {
                return null;
            }

            List<DeviceConfiguration> kept = new List<DeviceConfiguration>();
            foreach (DeviceConfiguration device in devices)
            {
                if (keep(device))
                {
                    kept.Add(device);
                }
            }

            return kept.ToArray();
        }

        private static DeviceConfiguration[] Copy(IReadOnlyList<DeviceConfiguration> devices)
        {
            DeviceConfiguration[] copy = new DeviceConfiguration[devices.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = devices[i];
            }

            return copy;
        }

        // Every sensor samples only at step states, so its period must be a whole number of steps
        // (an actuator has none, DeviceConfiguration.IsValid):
        // otherwise an Argus sensor samples unevenly under a period it never keeps (CORE-18). Basilisk
        // also needs whole steps; BasiliskEngine checks those in exact nanoseconds. A sensor whose
        // frames carry truth may not be controller-visible, because the gateway forwards those frames
        // unchanged (D7, IDeviceKind.MayBeControllerVisible). Device IDs are unique across the run.
        // Cameras that share a profile ID share its parameter set, so the recorded profile_id names
        // one set (ICameraKind, CORE-6); the profile is read only once the device is valid.
        private static bool AreDevicesValid(IReadOnlyList<DeviceConfiguration> devices, double fixedStepSeconds)
        {
            HashSet<string> deviceIds = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, CameraProfile> cameraProfiles = new Dictionary<string, CameraProfile>(StringComparer.Ordinal);
            foreach (DeviceConfiguration device in devices)
            {
                if (!device.IsValid ||
                    (!device.IsActuator && !IsWholeNumberOfSteps(device.Definition.SamplePeriodSeconds, fixedStepSeconds)) ||
                    (device.Definition.IsControllerVisible && !device.MayBeControllerVisible) ||
                    !deviceIds.Add(device.Definition.SensorId) ||
                    (device.KindDefinition is ICameraKind camera && !IsConsistent(cameraProfiles, camera.Profile(device))))
                {
                    return false;
                }
            }

            return true;
        }

        // The first camera with a profile ID sets its parameter set; every later one must match it.
        private static bool IsConsistent(Dictionary<string, CameraProfile> profiles, CameraProfile profile)
        {
            if (profiles.TryGetValue(profile.ProfileId, out CameraProfile first))
            {
                return profile.IsSameParameterSetAs(first);
            }

            profiles.Add(profile.ProfileId, profile);
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
