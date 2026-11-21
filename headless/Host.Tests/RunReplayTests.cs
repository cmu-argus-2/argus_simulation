using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Argus.Simulation.Core;
using NUnit.Framework;
using PbSim = Argus.Contracts.Sim.V1;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Host.Tests
{
    // HOST-7: a run recorded as headless/Host records it replays over the state stream as the live
    // run streamed, and the same way every time: truth with its environment and producing command,
    // the backend samples, and each sensor's latest frame, Valid or Unavailable.
    public sealed class RunReplayTests
    {
        [Test]
        public void AReplayPublishesTheLiveRunsPairsEveryTime()
        {
            string root = Path.Combine(Path.GetTempPath(), "argus-replay-" + Guid.NewGuid().ToString("N"));
            try
            {
                // P0's devices over a stand-in for Basilisk, so the run has everything a Basilisk run has.
                SimulationConfiguration file = RunConfigurationFile.Load(HostOptions.DefaultRunConfigurationPath, "replay-test");
                SimulationConfiguration p0 = new SimulationConfiguration(file.RunId, file.EpochUtc, file.FixedStepSeconds, devices: file.Devices);
                Observation observation;
                StateStreamPublisher livePublisher = new StateStreamPublisher();
                List<PbStream.SubscribeResponse> live = new List<PbStream.SubscribeResponse>();
                using (RunRecorder recorder = new RunRecorder(root, "test"))
                {
                    SimulationGateway gateway = new SimulationGateway(new BasiliskStandIn());
                    gateway.RunStarted += (run, sensors) => livePublisher.BeginGeneration(run);
                    gateway.StateProduced += livePublisher.PublishState;
                    gateway.SensorOutputProduced += livePublisher.PublishSensorOutput;
                    gateway.SensorOutputProduced += output => live.Add(livePublisher.Latest.Response);
                    gateway.RunStarted += (run, sensors) => recorder.BeginRun(run, sensors);
                    gateway.StateProduced += recorder.RecordState;
                    gateway.SensorOutputProduced += recorder.RecordSensorOutput;
                    gateway.CommandLogged += recorder.RecordCommand;
                    observation = gateway.Reset(0, p0);
                    for (int step = 0; step < 150; step++)
                    {
                        DeviceValues torque = new DeviceValues.Builder()
                            .Add(IdealBodyTorqueKind.TorqueKey, new Vector3d(1e-5 * (step % 7), -2e-5, 0.0))
                            .Build();
                        observation = gateway.Step(new DeviceCommandSet(
                            observation.Sequence,
                            observation.SimulationTimeSeconds,
                            new[] { new DeviceCommand("torque.body", torque) }));
                    }

                    gateway.Finish();
                    recorder.Close();
                }

                RecordedRun recorded = RecordedRun.Open(Directory.GetDirectories(root).Single());
                List<PbStream.SubscribeResponse>[] replays = { Replay(recorded), Replay(recorded) };

                Assert.That(observation.IsTerminal, Is.False, observation.TerminationReason);
                // What the comparison covers: environment, producing commands with torque, backend
                // samples including one sampled without data, and Valid and Unavailable frames.
                Assert.That(live.All(response => response.State.Environment != null));
                Assert.That(live.Count(response => response.State.AppliedCommands?.Commands.Any(c => c.Values.Count > 0) == true), Is.EqualTo(150));
                Assert.That(live[0].State.BackendSamples.Single(sample => sample.SensorId == "imu.main").Output, Is.Empty);
                Assert.That(live.Sum(response => response.State.BackendSamples.Count(sample => sample.Output.Count > 0)), Is.GreaterThan(150));
                Assert.That(live[0].SensorOutput.Frames.Single(frame => frame.SensorId == "imu.main").Status, Is.EqualTo(PbSim.SensorFrameStatus.Unavailable));
                Assert.That(live[^1].SensorOutput.Frames.Count(frame => frame.Status == PbSim.SensorFrameStatus.Valid), Is.EqualTo(11));
                Assert.That(live.All(response => response.Source == PbStream.RunSource.Live));
                foreach (List<PbStream.SubscribeResponse> replay in replays)
                {
                    Assert.That(replay.All(response => response.Source == PbStream.RunSource.Replay));
                    Assert.That(replay.Select(response => response.RunGeneration).Distinct().Count(), Is.EqualTo(1));
                    Assert.That(replay[0].RunGeneration, Is.Not.EqualTo(live[0].RunGeneration));
                    // Apart from the generation and the source, each pair is the live run's.
                    Assert.That(replay.Select(WithoutIdentity), Is.EqualTo(live.Select(WithoutIdentity)));
                }

                Assert.That(replays[0][0].RunGeneration, Is.Not.EqualTo(replays[1][0].RunGeneration));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static List<PbStream.SubscribeResponse> Replay(RecordedRun recorded)
        {
            StateStreamPublisher publisher = new StateStreamPublisher();
            List<PbStream.SubscribeResponse> responses = new List<PbStream.SubscribeResponse>();
            long states = RunReplay.Play(recorded, publisher, 1e6, CancellationToken.None, state => responses.Add(publisher.Latest.Response));
            Assert.That(states, Is.EqualTo(151));
            return responses;
        }

        // The analytic fixture's orbit, tagged ITRF93 with a made-up environment, every backend sensor
        // measured on its period (the IMU sampled without data at t_0, as Basilisk's imuSensor) and
        // every command applied, so a run has each part a Basilisk run's states have. Values are
        // arbitrary but differ per step and sensor.
        private sealed class BasiliskStandIn : ISimulationEngine
        {
            private readonly AnalyticSimulationEngine _orbit = new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0);
            private SimulationConfiguration _run;

            public string BackendName => "basilisk-stand-in";
            public bool IsInitialized => _orbit.IsInitialized;
            public bool AppliesActuatorCommands => true;

            public void Initialize(SimulationConfiguration configuration)
            {
                _run = configuration;
                _orbit.Initialize(configuration);
            }

            public bool TryReset(out SimulationState state) => Extend(_orbit.TryReset(out state), null, ref state);

            public bool TryStep(SimulationStepInput input, out SimulationState state) =>
                Extend(_orbit.TryStep(input, out state), input.Commands, ref state);

            private bool Extend(bool ok, DeviceCommandSet? command, ref SimulationState state)
            {
                if (!ok)
                {
                    return false;
                }

                SpacecraftState truth = state.Spacecraft;
                long k = truth.Sequence;
                Dictionary<string, object> measurements = new Dictionary<string, object>(StringComparer.Ordinal);
                List<string> unavailable = new List<string>();
                foreach (DeviceConfiguration device in _run.BackendSensors)
                {
                    long period = (long)Math.Round(device.Definition.SamplePeriodSeconds / _run.FixedStepSeconds);
                    if (k % period != 0)
                    {
                        continue;
                    }

                    if (k == 0 && device.Kind == "imu")
                    {
                        unavailable.Add(device.Definition.SensorId);
                        continue;
                    }

                    IBackendSensorKind kind = (IBackendSensorKind)device.KindDefinition;
                    DeviceValues.Builder output = new DeviceValues.Builder();
                    double value = 0.25 + (k % 11) * 0.01 + measurements.Count * 0.001;
                    foreach (DeviceKey key in kind.OutputKeys)
                    {
                        output = key.Type == DeviceValueType.Vector3
                            ? output.Add(key.Name, new Vector3d(value, -value, 2.0 * value))
                            : output.Add(key.Name, value);
                    }

                    measurements.Add(device.Definition.SensorId, kind.ReadOutput(output.Build()));
                }

                state = new SimulationState(
                    state.RunId,
                    BackendName,
                    new SpacecraftState(
                        k,
                        truth.SimulationTimeSeconds,
                        truth.TimestampUtc,
                        truth.PositionEcefMeters,
                        truth.VelocityEcefMetersPerSecond,
                        truth.BodyToEcef,
                        truth.AngularVelocityBodyRadiansPerSecond,
                        ReferenceFrame.Itrf93),
                    command,
                    new EnvironmentState(
                        new Vector3d(1.4e11, 2.0e10 + k * 1.0e6, 3.0e9),
                        new Quaterniond(0.0, 0.0, Math.Sin(k * 1e-4), Math.Cos(k * 1e-4)),
                        new Vector3d(0.0, 0.0, 7.292115e-5),
                        (k % 4) / 3.0),
                    new SensorMeasurementSet(measurements, unavailable));
                return true;
            }
        }

        private static PbStream.SubscribeResponse WithoutIdentity(PbStream.SubscribeResponse response)
        {
            PbStream.SubscribeResponse copy = response.Clone();
            copy.RunGeneration = "";
            copy.Source = PbStream.RunSource.Unspecified;
            return copy;
        }
    }
}
