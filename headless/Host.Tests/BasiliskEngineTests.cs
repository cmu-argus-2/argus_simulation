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

        // The configured_sensor_ids echo is checked against the configuration's backend-modelled
        // sensors, not against the request the mapper built, so a mapper that drops a sensor fails
        // at ConfigureRun (CORE-18 review). Here the echo is what the service returns for the request.
        [Test]
        public void ConfiguredSensorEchoMatchesTheConfigurationsBackendSensors()
        {
            SimulationConfiguration configuration = P0WithCamera();
            string[] echo = BasiliskProtoMapper.ToProto(configuration, 100_000_000).Sensors
                .Select(sensor => sensor.Definition.SensorId)
                .ToArray();

            Assert.That(echo, Does.Not.Contain("cam.px"));
            Assert.That(BasiliskEngine.HasSensorsInOrder(echo, configuration.Sensors), Is.True);
            Assert.That(BasiliskEngine.HasSensorsInOrder(echo.Skip(1).ToArray(), configuration.Sensors), Is.False);
            Assert.That(BasiliskEngine.HasSensorsInOrder(echo.Take(echo.Length - 1).ToArray(), configuration.Sensors), Is.False);
            Assert.That(BasiliskEngine.HasSensorsInOrder(echo.Reverse().ToArray(), configuration.Sensors), Is.False);
            Assert.That(BasiliskEngine.HasSensorsInOrder(echo.Append("cam.px").ToArray(), configuration.Sensors), Is.False);
        }

        // The P0 run file with an Argus-computed camera second in its sensor list.
        private static SimulationConfiguration P0WithCamera()
        {
            SimulationConfiguration p0 = RunConfigurationFile.Load(HostOptions.DefaultRunConfigurationPath, "host-test");
            List<SensorConfiguration> sensors = new List<SensorConfiguration>(p0.Sensors);
            sensors.Insert(1, SensorConfiguration.ForCamera(
                new SensorDefinition("cam.px", "arducam-imx708", "cam_px", 1.0, "argus", SensorMount.Identity),
                new CameraProfile(new CameraIntrinsics(4608, 2592, 3548.0, 3548.0, 2304.0, 1296.0), 0.0005)));
            return new SimulationConfiguration(
                p0.RunId,
                p0.EpochUtc,
                p0.FixedStepSeconds,
                initialOrbit: p0.InitialOrbit,
                randomSeed: p0.RandomSeed,
                kernelSetId: p0.KernelSetId,
                spacecraft: p0.Spacecraft,
                sensors: sensors);
        }
    }
}
