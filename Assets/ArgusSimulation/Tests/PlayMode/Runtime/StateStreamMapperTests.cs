using System;
using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using Google.Protobuf.WellKnownTypes;
using NUnit.Framework;
using PbSim = Argus.Contracts.Sim.V1;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Tests
{
    // The state stream decodes into Core through each frame's kind, keeps presence (a zero shadow
    // factor, an Unavailable sample), and rejects a response that breaks CON-1 (UNITY-5).
    public sealed class StateStreamMapperTests
    {
        private static readonly DateTime Epoch = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        [Test]
        public void APairDecodesThroughEachFramesKind()
        {
            StreamPair pair = StateStreamMapper.ToCore(Response());

            Assert.That(pair.RunGeneration, Is.EqualTo("generation-a"));
            SimulationState state = pair.State;
            Assert.That(state.Spacecraft.Sequence, Is.EqualTo(12));
            Assert.That(state.Spacecraft.EarthFixedFrame, Is.EqualTo(ReferenceFrame.Itrf93));
            Assert.That(state.Environment.Value.SpacecraftShadowFactor, Is.EqualTo(0.0));
            Assert.That(state.AppliedCommands.Value.Commands[0].Values.Vector3("torque_body_n_m"), Is.EqualTo(new Vector3d(0.0, 0.0, 0.0)));
            Assert.That(state.SensorMeasurements.TryGetMeasurement("mag.main", out MagnetometerMeasurement field), Is.True);
            Assert.That(field.MagneticFieldSensorTesla.Z, Is.EqualTo(2.5e-5));
            Assert.That(state.SensorMeasurements.WasSampled("imu.main"), Is.True);
            Assert.That(state.SensorMeasurements.TryGetMeasurement("imu.main", out ImuMeasurement _), Is.False);

            Assert.That(pair.Output.TryGetFrame("mag.main", out SensorFrame<MagnetometerMeasurement> mag), Is.True);
            Assert.That((mag.Status, mag.StateSequence, mag.Payload.MagneticFieldSensorTesla.Z), Is.EqualTo((SensorFrameStatus.Valid, 12L, 2.5e-5)));
            Assert.That(pair.Output.TryGetFrame("imu.main", out SensorFrame<ImuMeasurement> imu), Is.True);
            Assert.That(imu.Status, Is.EqualTo(SensorFrameStatus.Unavailable));
        }

        [Test]
        public void AResponseThatBreaksTheContractIsRejected()
        {
            PbStream.SubscribeResponse noGeneration = Response();
            noGeneration.RunGeneration = "";
            PbStream.SubscribeResponse emptyValid = Response();
            emptyValid.SensorOutput.Frames[0].Payload.Clear();
            PbStream.SubscribeResponse unknownKind = Response();
            unknownKind.SensorOutput.Frames[0].Kind = "star_tracker";

            Assert.That(() => StateStreamMapper.ToCore(noGeneration), Throws.TypeOf<FormatException>().With.Message.StartsWith("run_generation"));
            Assert.That(() => StateStreamMapper.ToCore(emptyValid), Throws.TypeOf<FormatException>().With.Message.StartsWith("sensor_output.frames[0].payload"));
            Assert.That(() => StateStreamMapper.ToCore(unknownKind), Throws.TypeOf<FormatException>().With.Message.StartsWith("sensor_output.frames[0].kind"));
        }

        // Step 12 of a Basilisk run in umbra: a magnetometer reading and the IMU's first, empty update.
        private static PbStream.SubscribeResponse Response()
        {
            Timestamp at = Timestamp.FromDateTime(Epoch.AddSeconds(1.2));
            PbSim.SimulationState state = new PbSim.SimulationState
            {
                RunId = "run",
                DynamicsBackend = "basilisk",
                Spacecraft = new PbSim.SpacecraftState
                {
                    Sequence = 12,
                    SimulationTimeS = 1.2,
                    TimestampUtc = at,
                    EarthFixedFrame = PbSim.ReferenceFrame.Itrf93,
                    PositionEarthFixedM = new PbSim.Vector3 { X = 6878137.0 },
                    VelocityEarthFixedMPerS = new PbSim.Vector3 { Y = 7600.0 },
                    BodyToEarthFixed = new PbSim.Quaternion { W = 1.0 },
                    AngularVelocityBodyRadPerS = new PbSim.Vector3()
                },
                AppliedCommands = new PbSim.AppliedCommand { Sequence = 11, ApplyAtSimulationTimeS = 1.1 },
                Environment = new PbSim.EnvironmentState
                {
                    SunPositionItrf93M = new PbSim.Vector3 { X = -1.47e11 },
                    J2000ToItrf93 = new PbSim.Quaternion { W = 1.0 },
                    EarthAngularVelocityItrf93RadPerS = new PbSim.Vector3 { Z = 7.292115e-5 },
                    SpacecraftShadowFactor = 0.0
                }
            };
            PbSim.DeviceCommand torque = new PbSim.DeviceCommand { DeviceId = "torque.body" };
            torque.Values.Add("torque_body_n_m", new PbSim.DeviceValue { Vector3 = new PbSim.Vector3() });
            state.AppliedCommands.Commands.Add(torque);
            PbSim.BackendSensorSample magSample = new PbSim.BackendSensorSample { SensorId = "mag.main" };
            magSample.Output.Add("magnetic_field_sensor_tesla", MagneticField());
            state.BackendSamples.Add(magSample);
            state.BackendSamples.Add(new PbSim.BackendSensorSample { SensorId = "imu.main" });

            PbSim.SensorOutputSet output = new PbSim.SensorOutputSet { RunId = "run", SpacecraftSequence = 12, SimulationTimeS = 1.2 };
            PbSim.SensorFrame mag = Frame("mag.main", "magnetometer", PbSim.SensorFrameStatus.Valid, at);
            mag.Payload.Add("magnetic_field_sensor_tesla", MagneticField());
            output.Frames.Add(mag);
            output.Frames.Add(Frame("imu.main", "imu", PbSim.SensorFrameStatus.Unavailable, at));
            return new PbStream.SubscribeResponse { State = state, SensorOutput = output, RunGeneration = "generation-a" };
        }

        private static PbSim.DeviceValue MagneticField() =>
            new PbSim.DeviceValue { Vector3 = new PbSim.Vector3 { Z = 2.5e-5 } };

        private static PbSim.SensorFrame Frame(string sensorId, string kind, PbSim.SensorFrameStatus status, Timestamp at) =>
            new PbSim.SensorFrame
            {
                RunId = "run",
                SensorId = sensorId,
                FrameId = sensorId.Replace('.', '_'),
                Sequence = 12,
                SimulationTimeS = 1.2,
                TimestampUtc = at,
                Status = status,
                Source = "basilisk",
                CaptureStateSequence = 12,
                Kind = kind
            };
    }
}
