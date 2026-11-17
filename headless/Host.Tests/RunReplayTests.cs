using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Argus.Simulation.Core;
using NUnit.Framework;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Host.Tests
{
    // HOST-7: a run recorded as headless/Host records it replays over the state stream as the live
    // run streamed, and the same way every time.
    public sealed class RunReplayTests
    {
        [Test]
        public void AReplayPublishesTheLiveRunsPairsEveryTime()
        {
            string root = Path.Combine(Path.GetTempPath(), "argus-replay-" + Guid.NewGuid().ToString("N"));
            try
            {
                // P0's devices on the analytic engine, as P0RecordingTests runs them without Basilisk.
                SimulationConfiguration file = RunConfigurationFile.Load(HostOptions.DefaultRunConfigurationPath, "replay-test");
                SimulationConfiguration p0 = new SimulationConfiguration(file.RunId, file.EpochUtc, file.FixedStepSeconds, devices: file.Devices);
                StateStreamPublisher livePublisher = new StateStreamPublisher();
                List<PbStream.SubscribeResponse> live = new List<PbStream.SubscribeResponse>();
                using (RunRecorder recorder = new RunRecorder(root, "test"))
                {
                    SimulationGateway gateway = new SimulationGateway(new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0));
                    gateway.RunStarted += (run, sensors) => livePublisher.BeginGeneration(run);
                    gateway.StateProduced += livePublisher.PublishState;
                    gateway.SensorOutputProduced += livePublisher.PublishSensorOutput;
                    gateway.SensorOutputProduced += output => live.Add(livePublisher.Latest.Response);
                    gateway.RunStarted += (run, sensors) => recorder.BeginRun(run, sensors);
                    gateway.StateProduced += recorder.RecordState;
                    gateway.SensorOutputProduced += recorder.RecordSensorOutput;
                    gateway.CommandLogged += recorder.RecordCommand;
                    gateway.Reset(0, p0);
                    for (int step = 0; step < 150; step++)
                    {
                        gateway.Step();
                    }

                    gateway.Finish();
                    recorder.Close();
                }

                RecordedRun recorded = RecordedRun.Open(Directory.GetDirectories(root).Single());
                List<PbStream.SubscribeResponse>[] replays = { Replay(recorded), Replay(recorded) };

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

        private static PbStream.SubscribeResponse WithoutIdentity(PbStream.SubscribeResponse response)
        {
            PbStream.SubscribeResponse copy = response.Clone();
            copy.RunGeneration = "";
            copy.Source = PbStream.RunSource.Unspecified;
            return copy;
        }
    }
}
