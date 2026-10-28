using Google.Protobuf;
using NUnit.Framework;
using PbSim = Argus.Contracts.Sim.V1;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Host.Tests
{
    // Wire round trips only: these do not validate a Core mapper or a running stream.
    public sealed class StateStreamContractTests
    {
        [Test]
        public void InitialAnalyticStatePreservesZeroAndMissingEnvironment()
        {
            var message = new PbStream.SubscribeResponse
            {
                State = new PbSim.SimulationState
                {
                    RunId = "test", DynamicsBackend = "analytic",
                    Spacecraft = new PbSim.SpacecraftState
                    {
                        Sequence = 0, SimulationTimeS = 0,
                        EarthFixedFrame = PbSim.ReferenceFrame.AnalyticEarthFixed
                    }
                }
            };
            var decoded = PbStream.SubscribeResponse.Parser.ParseFrom(message.ToByteArray());
            Assert.That(decoded.State.Spacecraft.HasSequence, Is.True);
            Assert.That(decoded.State.Spacecraft.Sequence, Is.Zero);
            Assert.That(decoded.State.Spacecraft.HasSimulationTimeS, Is.True);
            Assert.That(decoded.State.Environment, Is.Null);
            Assert.That(decoded.State.AppliedCommands, Is.Null);
            var umbra = PbSim.EnvironmentState.Parser.ParseFrom(
                new PbSim.EnvironmentState { SpacecraftShadowFactor = 0 }.ToByteArray());
            Assert.That(umbra.HasSpacecraftShadowFactor, Is.True);
            Assert.That(new PbSim.EnvironmentState().HasSpacecraftShadowFactor, Is.False);
        }

        [Test]
        public void PairedSnapshotPreservesUnavailableFrameCaptureMetadata()
        {
            var output = new PbSim.SensorOutputSet
            {
                RunId = "test", SpacecraftSequence = 105, SimulationTimeS = 1.05
            };
            output.Frames.Add(new PbSim.SensorFrame
            {
                RunId = "test", SensorId = "imu", FrameId = "imu-body", Source = "backend",
                Sequence = 7, SimulationTimeS = 1, CaptureStateSequence = 100,
                Status = PbSim.SensorFrameStatus.Unavailable
            });
            var decoded = PbStream.SubscribeResponse.Parser.ParseFrom(
                new PbStream.SubscribeResponse { SensorOutput = output, State = new PbSim.SimulationState
                { RunId = "test", Spacecraft = new PbSim.SpacecraftState { Sequence = 105, SimulationTimeS = 1.05 } } }.ToByteArray());
            Assert.That(decoded.State.RunId, Is.EqualTo(decoded.SensorOutput.RunId));
            Assert.That(decoded.State.Spacecraft.Sequence, Is.EqualTo(decoded.SensorOutput.SpacecraftSequence));
            Assert.That(decoded.State.Spacecraft.SimulationTimeS, Is.EqualTo(decoded.SensorOutput.SimulationTimeS));
            Assert.That(decoded.SensorOutput.SpacecraftSequence, Is.EqualTo(105));
            var frame = decoded.SensorOutput.Frames[0];
            Assert.That(frame.Sequence, Is.EqualTo(7));
            Assert.That(frame.CaptureStateSequence, Is.EqualTo(100));
            Assert.That(frame.SimulationTimeS, Is.EqualTo(1));
            Assert.That(frame.PayloadCase, Is.EqualTo(PbSim.SensorFrame.PayloadOneofCase.None));
            frame.ClearCaptureStateSequence();
            var unknown = PbSim.SensorFrame.Parser.ParseFrom(frame.ToByteArray());
            // Wire can express absence; a consumer must reject this missing required field.
            Assert.That(unknown.HasCaptureStateSequence, Is.False);
            frame.CaptureStateSequence = 0;
            Assert.That(PbSim.SensorFrame.Parser.ParseFrom(frame.ToByteArray()).HasCaptureStateSequence, Is.True);
        }

        [Test]
        public void BackendUnavailableIsDifferentFromMissingSampleAndEmptyResult()
        {
            var state = new PbSim.SimulationState();
            state.BackendSamples.Add(new PbSim.BackendSensorSample
            {
                SensorId = "imu", Unavailable = new PbSim.UnavailableMeasurement()
            });
            var decoded = PbSim.SimulationState.Parser.ParseFrom(state.ToByteArray());
            Assert.That(decoded.BackendSamples.Count, Is.EqualTo(1));
            Assert.That(decoded.BackendSamples[0].ResultCase,
                Is.EqualTo(PbSim.BackendSensorSample.ResultOneofCase.Unavailable));
            Assert.That(new PbSim.BackendSensorSample().ResultCase,
                Is.EqualTo(PbSim.BackendSensorSample.ResultOneofCase.None));
        }
    }
}
