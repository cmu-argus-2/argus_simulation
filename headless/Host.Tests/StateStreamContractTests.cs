using System;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NUnit.Framework;
using PbSim = Argus.Contracts.Sim.V1;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Host.Tests
{
    // Wire/schema tests, not a running HOST-3 cache, pairing validator or Unity client.
    internal sealed class StateStreamContractTests
    {
        private static readonly DateTime Epoch = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        private static PbStream.SubscribeResponse Snapshot(string generation, long step)
        {
            return new PbStream.SubscribeResponse
            {
                RunGeneration = generation,
                State = new PbSim.SimulationState
                {
                    RunId = "test", DynamicsBackend = "analytic",
                    Spacecraft = new PbSim.SpacecraftState
                    {
                        Sequence = step, SimulationTimeS = step * 0.1,
                        TimestampUtc = Timestamp.FromDateTime(Epoch.AddSeconds(step * 0.1)),
                        EarthFixedFrame = PbSim.ReferenceFrame.AnalyticEarthFixed,
                        PositionEarthFixedM = new PbSim.Vector3 { X = 7000000 },
                        VelocityEarthFixedMPerS = new PbSim.Vector3 { Y = 7500 },
                        BodyToEarthFixed = new PbSim.Quaternion { W = 1 },
                        AngularVelocityBodyRadPerS = new PbSim.Vector3()
                    }
                },
                SensorOutput = new PbSim.SensorOutputSet
                {
                    RunId = "test", SpacecraftSequence = step, SimulationTimeS = step * 0.1
                }
            };
        }

        private static PbSim.SensorFrame Frame(PbSim.SensorFrameStatus status)
        {
            var frame = new PbSim.SensorFrame
            {
                RunId = "test", SensorId = "mag", FrameId = "mag-body", Source = "backend", Kind = "magnetometer",
                Sequence = 7, SimulationTimeS = 1, CaptureStateSequence = 10,
                TimestampUtc = Timestamp.FromDateTime(Epoch.AddSeconds(1)), Status = status
            };
            if (status == PbSim.SensorFrameStatus.Valid || status == PbSim.SensorFrameStatus.Stale)
                frame.Payload.Add("magnetic_field_sensor_tesla", new PbSim.DeviceValue
                { Vector3 = new PbSim.Vector3 { Z = 0.000025 } });
            return frame;
        }

        private static PbStream.SubscribeResponse RoundTrip(PbStream.SubscribeResponse value) =>
            PbStream.SubscribeResponse.Parser.ParseFrom(value.ToByteArray());

        [Test]
        public void InitialAnalyticSnapshotPreservesPresentZeroAndAbsentEnvironment()
        {
            var decoded = RoundTrip(Snapshot("generation-a", 0));
            Assert.That(decoded.State.Spacecraft.HasSequence, Is.True);
            Assert.That(decoded.State.Spacecraft.Sequence, Is.Zero);
            Assert.That(decoded.State.Spacecraft.HasSimulationTimeS, Is.True);
            Assert.That(decoded.SensorOutput, Is.Not.Null);
            Assert.That(decoded.SensorOutput.Frames, Is.Empty);
            Assert.That(decoded.State.Environment, Is.Null);
            Assert.That(decoded.State.AppliedCommands, Is.Null);
            Assert.That(new PbSim.SpacecraftState().HasSequence, Is.False);
        }

        [Test]
        public void SameRunIdResetRetainsDistinctGenerationDespiteSequenceRestart()
        {
            var old = RoundTrip(Snapshot("generation-a", 4000));
            var reset = RoundTrip(Snapshot("generation-b", 0));
            var reconnect = RoundTrip(Snapshot("generation-b", 1));
            Assert.That(old.State.RunId, Is.EqualTo(reset.State.RunId));
            Assert.That(old.RunGeneration, Is.EqualTo("generation-a"));
            Assert.That(reset.RunGeneration, Is.EqualTo("generation-b"));
            Assert.That(reset.State.Spacecraft.Sequence, Is.Zero);
            Assert.That(reconnect.RunGeneration, Is.EqualTo(reset.RunGeneration));
        }

        [TestCase(PbSim.SensorFrameStatus.Valid)]
        [TestCase(PbSim.SensorFrameStatus.Stale)]
        [TestCase(PbSim.SensorFrameStatus.Unavailable)]
        [TestCase(PbSim.SensorFrameStatus.Invalid)]
        public void CachedSensorRetainsAcquisitionMetadataAcrossLaterSnapshots(PbSim.SensorFrameStatus status)
        {
            var first = Snapshot("generation-a", 20);
            first.SensorOutput.Frames.Add(Frame(status));
            var later = Snapshot("generation-a", 30);
            later.SensorOutput.Frames.Add(first.SensorOutput.Frames[0].Clone());
            var decoded = RoundTrip(later);
            var frame = decoded.SensorOutput.Frames[0];
            Assert.That(decoded.State.Spacecraft.Sequence, Is.EqualTo(30));
            Assert.That(frame.CaptureStateSequence, Is.EqualTo(10));
            Assert.That(frame.SimulationTimeS, Is.EqualTo(1));
            Assert.That(frame.TimestampUtc, Is.EqualTo(Timestamp.FromDateTime(Epoch.AddSeconds(1))));
            Assert.That(frame.Status, Is.EqualTo(status));
            Assert.That(frame.ToByteArray(), Is.EqualTo(RoundTrip(first).SensorOutput.Frames[0].ToByteArray()));
            bool hasData = status == PbSim.SensorFrameStatus.Valid || status == PbSim.SensorFrameStatus.Stale;
            Assert.That(frame.Kind, Is.EqualTo("magnetometer"));
            Assert.That(frame.Payload.Count, Is.EqualTo(hasData ? 1 : 0));
            if (hasData)
                Assert.That(frame.Payload["magnetic_field_sensor_tesla"].Vector3.Z, Is.EqualTo(0.000025));
        }

        [Test]
        public void EnvironmentShadowZeroAndBackendUnavailableKeepPresence()
        {
            var snapshot = Snapshot("generation-a", 0);
            snapshot.State.Spacecraft.EarthFixedFrame = PbSim.ReferenceFrame.Itrf93;
            snapshot.State.Environment = new PbSim.EnvironmentState
            {
                SunPositionItrf93M = new PbSim.Vector3 { X = 1.5e11 },
                J2000ToItrf93 = new PbSim.Quaternion { W = 1 },
                EarthAngularVelocityItrf93RadPerS = new PbSim.Vector3 { Z = 7.292115e-5 },
                SpacecraftShadowFactor = 0
            };
            snapshot.State.BackendSamples.Add(new PbSim.BackendSensorSample
            { SensorId = "imu" });
            var decoded = RoundTrip(snapshot);
            Assert.That(decoded.State.Environment.HasSpacecraftShadowFactor, Is.True);
            Assert.That(decoded.State.Environment.SpacecraftShadowFactor, Is.Zero);
            Assert.That(new PbSim.EnvironmentState().HasSpacecraftShadowFactor, Is.False);
            Assert.That(decoded.State.BackendSamples.Count, Is.EqualTo(1));
            Assert.That(decoded.State.BackendSamples[0].Output, Is.Empty);
        }

        [Test]
        public void DeviceMapsCarryBackendOutputsAndCompleteAppliedCommands()
        {
            var snapshot = Snapshot("generation-a", 1);
            var sample = new PbSim.BackendSensorSample { SensorId = "light.px" };
            sample.Output.Add("irradiance_w_per_m2", new PbSim.DeviceValue { Number = 0 });
            snapshot.State.BackendSamples.Add(sample);
            snapshot.State.AppliedCommands = new PbSim.AppliedCommand { Sequence = 0, ApplyAtSimulationTimeS = 0 };
            // No actuator kinds are configured yet: present producing command with empty list.
            var decoded = RoundTrip(snapshot);
            Assert.That(decoded.State.AppliedCommands.Commands, Is.Empty);
            Assert.That(decoded.State.AppliedCommands.HasApplyAtSimulationTimeS, Is.True);
            var value = decoded.State.BackendSamples[0].Output["irradiance_w_per_m2"];
            Assert.That(value.ValueCase, Is.EqualTo(PbSim.DeviceValue.ValueOneofCase.Number));
            Assert.That(value.Number, Is.Zero);
            // Wire-shape check for a future actuator, not a claim the kind is implemented.
            var command = new PbSim.DeviceCommand { DeviceId = "actuator.test" };
            command.Values.Add("torque_body_n_m", new PbSim.DeviceValue { Vector3 = new PbSim.Vector3 { X = 1 } });
            snapshot.State.AppliedCommands.Commands.Add(command);
            Assert.That(RoundTrip(snapshot).State.AppliedCommands.Commands[0].Values["torque_body_n_m"].Vector3.X,
                Is.EqualTo(1));
        }

        [Test]
        public void SchemaKeepsBothMessagesAndGenerationOutsideAOneof()
        {
            var descriptor = PbStream.SubscribeResponse.Descriptor;
            Assert.That(descriptor.FindFieldByName("state").ContainingOneof, Is.Null);
            Assert.That(descriptor.FindFieldByName("sensor_output").ContainingOneof, Is.Null);
            Assert.That(descriptor.FindFieldByName("run_generation").FieldNumber, Is.EqualTo(3));
            Assert.That(descriptor.FindFieldByName("state").MessageType, Is.EqualTo(PbSim.SimulationState.Descriptor));
            Assert.That(descriptor.FindFieldByName("sensor_output").MessageType, Is.EqualTo(PbSim.SensorOutputSet.Descriptor));
        }
    }
}
