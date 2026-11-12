using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Argus.Simulation.Core;
using NUnit.Framework;
using PbSim = Argus.Contracts.Sim.V1;

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

        // Each per-device command becomes one DeviceCommand with the same ID and values, in the order
        // the gateway sent them (system-architecture §11 command application).
        [Test]
        public void PerDeviceCommandsMapToTheProtoInOrder()
        {
            DeviceCommandSet command = new DeviceCommandSet(4, 0.4, new[]
            {
                new DeviceCommand("torque.body", new DeviceValues.Builder()
                    .Add(IdealBodyTorqueKind.TorqueKey, new Vector3d(1e-4, -2e-4, 3e-4))
                    .Build()),
                new DeviceCommand("mtq.xp", new DeviceValues.Builder().Add("dipole_a_m2", -0.0814).Build())
            });

            PbSim.DeviceCommandSet message = BasiliskProtoMapper.ToProto(command);

            Assert.That(message.Commands.Select(device => device.DeviceId), Is.EqualTo(new[] { "torque.body", "mtq.xp" }));
            PbSim.Vector3 torque = message.Commands[0].Values[IdealBodyTorqueKind.TorqueKey].Vector3;
            Assert.That((torque.X, torque.Y, torque.Z), Is.EqualTo((1e-4, -2e-4, 3e-4)));
            Assert.That(message.Commands[0].Values.Count, Is.EqualTo(1));
            Assert.That(message.Commands[1].Values["dipole_a_m2"].Number, Is.EqualTo(-0.0814));
            Assert.That(BasiliskProtoMapper.ToProto(DeviceCommandSet.None(0, 0.0)).Commands, Is.Empty);
        }

        // BSK-8's end-to-end check, run against a running Basilisk service (default 127.0.0.1:50051,
        // or $ARGUS_BASILISK_ENDPOINT); CI runs it in the Basilisk job. The P0 body starts at rest with
        // a diagonal inertia, so a torque about z changes only the z rate, by torque x dt / Izz, over
        // the step its command is for, and the rate holds once the torque is idle again. The command
        // asks for twice P0's 1 mN·m limit, so the gateway clamps it (CORE-10) and the service, which
        // refuses anything past the limit, runs it at the limit.
        [Test, Explicit("Needs the Argus.Basilisk service running")]
        public void AGatewayOverTheServiceAppliesATorqueOverItsStep()
        {
            SimulationConfiguration p0 = RunConfigurationFile.Load(HostOptions.DefaultRunConfigurationPath, "host-actuator-test");
            SimulationConfiguration scenario = new SimulationConfiguration(
                p0.RunId,
                p0.EpochUtc,
                p0.FixedStepSeconds,
                initialOrbit: p0.InitialOrbit,
                randomSeed: p0.RandomSeed,
                kernelSetId: p0.KernelSetId,
                spacecraft: p0.Spacecraft,
                devices: p0.Devices.Where(device => device.KindDefinition is IdealBodyTorqueKind).ToList());
            Assert.That(scenario.Devices.Select(device => device.Definition.SensorId), Is.EqualTo(new[] { "torque.body" }));
            string endpoint = Environment.GetEnvironmentVariable(HostOptions.EndpointVariable) ?? "127.0.0.1:50051";

            using BasiliskEngine engine = new BasiliskEngine(new Uri($"http://{endpoint}"), 0.0, CancellationToken.None);
            SimulationGateway gateway = new SimulationGateway(engine);
            List<SimulationState> states = new List<SimulationState>();
            List<CommandRecord> log = new List<CommandRecord>();
            gateway.StateProduced += states.Add;
            gateway.CommandLogged += log.Add;
            gateway.Reset(0, scenario);
            double dt = scenario.FixedStepSeconds;
            const double TorqueZ = 1e-3;
            const double RequestedTorqueZ = 2e-3;
            gateway.Step(DeviceCommandSet.None(0, 0.0));
            gateway.Step(new DeviceCommandSet(1, dt, new[]
            {
                new DeviceCommand("torque.body", new DeviceValues.Builder()
                    .Add(IdealBodyTorqueKind.TorqueKey, new Vector3d(0.0, 0.0, RequestedTorqueZ))
                    .Build())
            }));
            gateway.Step(DeviceCommandSet.None(2, 2.0 * dt));

            Assert.That(log.Select(record => record.Outcome), Is.EqualTo(new[] { CommandOutcome.Applied, CommandOutcome.Clamped, CommandOutcome.Applied }));
            Assert.That(log[1].Applied.Value.Commands[0].Values.Vector3(IdealBodyTorqueKind.TorqueKey), Is.EqualTo(new Vector3d(0.0, 0.0, TorqueZ)));
            Assert.That(states.Count, Is.EqualTo(4));
            Vector3d[] rates = states.Select(state => state.Spacecraft.AngularVelocityBodyRadiansPerSecond).ToArray();
            double izz = scenario.Spacecraft.Value.InertiaBodyKilogramSquareMeters.Row2.Z;
            double expected = TorqueZ * dt / izz;
            Assert.That(rates[1], Is.EqualTo(rates[0]));
            Assert.That(rates[2].Z - rates[1].Z, Is.EqualTo(expected).Within(1e-12));
            Assert.That((rates[2].X, rates[2].Y), Is.EqualTo((0.0, 0.0)));
            Assert.That(rates[3].Z, Is.EqualTo(rates[2].Z).Within(1e-15));
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
