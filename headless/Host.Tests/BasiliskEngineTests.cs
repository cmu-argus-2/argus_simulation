using System.Collections.Generic;
using System.Linq;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Host.Tests
{
    public sealed class BasiliskEngineTests
    {
        // The command's start time travels as exact integer nanoseconds. Its seconds value drifts:
        // 81923 x 0.1 s is 8192.300000000001 s, which is not a whole number of nanoseconds.
        [TestCase(0L, 0UL)]
        [TestCase(81_923L, 8_192_300_000_000UL)]
        [TestCase(10_000_000L, 1_000_000_000_000_000UL)]
        public void CommandTimeIsExactOnTheStepGridForLongRuns(long sequence, ulong expected)
        {
            Assert.That(BasiliskEngine.CommandTimeNanoseconds(sequence, 100_000_000), Is.EqualTo(expected));
        }

        // The configured_device_ids echo is checked against the configuration's backend-modelled
        // devices, not against the request the mapper built, so a mapper that drops a device fails
        // at ConfigureRun (CORE-18 review). Here the echo is what the service returns for the request.
        [Test]
        public void ConfiguredDeviceEchoMatchesTheConfigurationsBackendDevices()
        {
            SimulationConfiguration configuration = P0WithCamera();
            string[] echo = BasiliskProtoMapper.ToProto(configuration, 100_000_000).Devices
                .Select(device => device.DeviceId)
                .ToArray();

            // Every backend device, in order, and no Argus-computed camera (P0 has its own four).
            Assert.That(echo, Is.EqualTo(configuration.Devices.Where(device => device.IsBackendModelled).Select(device => device.Definition.SensorId)));
            Assert.That(BasiliskEngine.HasSensorsInOrder(echo, configuration.Devices), Is.True);
            Assert.That(BasiliskEngine.HasSensorsInOrder(echo.Skip(1).ToArray(), configuration.Devices), Is.False);
            Assert.That(BasiliskEngine.HasSensorsInOrder(echo.Take(echo.Length - 1).ToArray(), configuration.Devices), Is.False);
            Assert.That(BasiliskEngine.HasSensorsInOrder(echo.Reverse().ToArray(), configuration.Devices), Is.False);
            Assert.That(BasiliskEngine.HasSensorsInOrder(echo.Append("cam.test").ToArray(), configuration.Devices), Is.False);
        }

        // The P0 run file with an Argus-computed camera second in its device list.
        private static SimulationConfiguration P0WithCamera()
        {
            SimulationConfiguration p0 = RunConfigurationFile.Load(HostOptions.DefaultRunConfigurationPath, "host-test");
            List<DeviceConfiguration> devices = new List<DeviceConfiguration>(p0.Devices);
            devices.Insert(1, CameraKind.Instance.Configure(
                new SensorDefinition("cam.test", "arducam-imx708", "cam_test", 1.0, "argus", SensorMount.Identity, isControllerVisible: false),
                new CameraProfile(
                    "imx708-66deg-spec-v0",
                    new CameraIntrinsics(4608, 2592, 3548.0, 3548.0, 2304.0, 1296.0),
                    "spec-estimate",
                    isCalibrated: false,
                    CameraDistortion.Zero("none"),
                    CameraProfile.NoNoise,
                    0.0005,
                    new CameraExtrinsicsProvenance("argus-2-config", "assumed-com", isOrientationVerified: false))));
            return new SimulationConfiguration(
                p0.RunId,
                p0.EpochUtc,
                p0.FixedStepSeconds,
                initialOrbit: p0.InitialOrbit,
                randomSeed: p0.RandomSeed,
                kernelSetId: p0.KernelSetId,
                spacecraft: p0.Spacecraft,
                devices: devices);
        }
    }
}
