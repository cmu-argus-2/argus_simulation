using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class RunRecorderTests
    {
        private string _root;

        [SetUp]
        public void CreateRoot()
        {
            _root = Path.Combine(Path.GetTempPath(), "argus-recorder-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void DeleteRoot()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Test]
        public void StatesReadBackWithContiguousSequencesOnTheStepGrid()
        {
            const int steps = 40;
            using (RunRecorder recorder = new RunRecorder(_root, "test"))
            {
                SimulationGateway gateway = RecordedGateway(recorder);
                gateway.Reset(3, Scenario(0.25));
                for (int i = 0; i < steps; i++)
                {
                    gateway.Step();
                }

                recorder.Close();
            }

            string run = Path.Combine(_root, "test-run_0");
            string[] states = File.ReadAllLines(Path.Combine(run, "states.jsonl"));
            Assert.That(states.Length, Is.EqualTo(steps + 1));
            for (int i = 0; i < states.Length; i++)
            {
                Assert.That(Field(states[i], "sequence"), Is.EqualTo(i.ToString(CultureInfo.InvariantCulture)));
                double time = double.Parse(Field(states[i], "simulation_time_s"), CultureInfo.InvariantCulture);
                Assert.That(SimulationTime.AreSame(time, i * 0.25), Is.True, $"line {i}: t = {time}");
            }

            Assert.That(File.ReadAllLines(Path.Combine(run, "sensors.jsonl")).Length, Is.EqualTo(steps + 1));
            string manifest = File.ReadAllText(Path.Combine(run, "manifest.json"));
            Assert.That(Field(manifest, "complete"), Is.EqualTo("true"));
            Assert.That(Field(manifest, "termination"), Is.EqualTo("null"));
            Assert.That(Field(manifest, "random_seed"), Is.EqualTo("3"));
            Assert.That(Field(manifest, "dropped_states"), Is.EqualTo("0"));
        }

        [Test]
        public void StalledWriterNeverDelaysTheStepLoop()
        {
            const int steps = 200;
            using ManualResetEventSlim release = new ManualResetEventSlim(false);
            using Timer timeout = new Timer(_ => release.Set(), null, 5000, Timeout.Infinite);
            using RunRecorder recorder = new RunRecorder(_root, "test", null, capacity: 16, () => release.Wait());
            SimulationGateway gateway = RecordedGateway(recorder);

            Stopwatch stopwatch = Stopwatch.StartNew();
            gateway.Reset(0, Scenario(0.1));
            for (int i = 0; i < steps; i++)
            {
                gateway.Step();
            }

            stopwatch.Stop();
            bool stillStalled = !release.IsSet;
            release.Set();
            recorder.Close();

            Assert.That(stillStalled, Is.True, "the writer was released before the step loop finished");
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));

            // Every state is either written or counted as dropped.
            string run = Path.Combine(_root, "test-run_0");
            string manifest = File.ReadAllText(Path.Combine(run, "manifest.json"));
            long dropped = long.Parse(Field(manifest, "dropped_states"), CultureInfo.InvariantCulture);
            Assert.That(Field(manifest, "policy"), Is.EqualTo("\"drop-newest\""));
            Assert.That(dropped, Is.GreaterThan(0));
            Assert.That(File.ReadAllLines(Path.Combine(run, "states.jsonl")).Length + dropped, Is.EqualTo(steps + 1));
        }

        [Test]
        public void ARunEndedByTheGatewayRecordsWhyAndWhere()
        {
            using (RunRecorder recorder = new RunRecorder(_root, "test"))
            {
                AnalyticSimulationEngine analytic = Analytic();
                FailingEngine engine = new FailingEngine(analytic, failAtStep: 3, new InvalidOperationException("Basilisk Step failed: Unavailable"));
                SimulationGateway gateway = RecordedGateway(recorder, engine, analytic.Orbit);
                gateway.Reset(0, Scenario(0.25));
                gateway.Step();
                gateway.Step();
                Assert.That(gateway.Step().IsTerminal, Is.True);
                recorder.Close();
            }

            string run = Path.Combine(_root, "test-run_0");
            Assert.That(File.ReadAllLines(Path.Combine(run, "states.jsonl")).Length, Is.EqualTo(3));
            string manifest = File.ReadAllText(Path.Combine(run, "manifest.json"));
            // The recorder closed cleanly, and the manifest says the run died at step 3, not that it was cut short.
            Assert.That(Field(manifest, "complete"), Is.EqualTo("true"));
            Assert.That(Field(manifest, "status"), Is.EqualTo("\"BackendFailed\""));
            Assert.That(Field(manifest, "last_sequence"), Is.EqualTo("2"));
            Assert.That(Field(manifest, "last_simulation_time_s"), Is.EqualTo("0.5"));
            Assert.That(Field(manifest, "reason"), Is.EqualTo("\"Step 3 failed: Basilisk Step failed: Unavailable\""));
        }

        [Test]
        public void EachRunIdNumbersItsOwnSegmentsAndNeverReusesOneOnDisk()
        {
            // A segment an earlier process left behind.
            Directory.CreateDirectory(Path.Combine(_root, "a_0"));
            using (RunRecorder recorder = new RunRecorder(_root, "test"))
            {
                foreach (string runId in new[] { "a", "b", "a" })
                {
                    recorder.BeginRun(new SimulationConfiguration(runId, DateTimeOffset.UnixEpoch, 0.5), Array.Empty<SensorDefinition>());
                    recorder.RecordState(State(runId));
                }

                recorder.Close();
            }

            Assert.That(File.Exists(Path.Combine(_root, "a_1", "manifest.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(_root, "b_0", "manifest.json")), Is.True);
            Assert.That(Field(File.ReadAllText(Path.Combine(_root, "a_2", "manifest.json")), "segment"), Is.EqualTo("2"));
            Assert.That(Directory.GetFileSystemEntries(Path.Combine(_root, "a_0")), Is.Empty);
        }

        // G3: a camera frame captured at state 10 and published at state 13 joins to state 10, and
        // so does one published Unavailable.
        [Test]
        public void CameraFramesRecordTheirCaptureStateNotTheirArrivalStep()
        {
            const string cameraId = "cam.test";
            Vector3d sun = new Vector3d(1.47e11, 2.0e10, -6.0e10);
            CameraIntrinsics intrinsics = new CameraIntrinsics(4, 2, 3.0, 3.0, 2.0, 1.0);
            RenderRequest Request(long sequence, long stateSequence) => new RenderRequest(
                cameraId,
                sequence,
                stateSequence,
                stateSequence * 0.1,
                DateTimeOffset.UnixEpoch.AddSeconds(stateSequence * 0.1),
                0.01,
                new Vector3d(6_878_137.0, 0.0, 0.0),
                new Quaterniond(0.0, 0.0, 0.0, 1.0),
                intrinsics,
                sun);
            SensorFrame<ImageFrame> Frame(RenderRequest request, SensorFrameStatus status, ImageFrame payload) =>
                new SensorFrame<ImageFrame>(
                    "test-run",
                    cameraId,
                    "cam_test",
                    request.Sequence,
                    request.StateSequence,
                    request.ExposureStartSimulationTimeSeconds,
                    request.ExposureStartUtc,
                    status,
                    "simulation",
                    payload);

            RenderRequest late = Request(0, 10);
            RenderRequest timedOut = Request(1, 11);
            using (RunRecorder recorder = new RunRecorder(_root, "test"))
            {
                recorder.BeginRun(
                    new SimulationConfiguration("test-run", DateTimeOffset.UnixEpoch, 0.1),
                    new[] { new SensorDefinition(cameraId, "test-camera", "cam_test", 1.0, "simulation", SensorMount.Identity) });
                recorder.RecordSensorOutput(new SensorOutputSet("test-run", 13, 1.3, new ISensorFrame[]
                {
                    Frame(late, SensorFrameStatus.Valid, new ImageFrame(late, ImageEncoding.Rgb8, new byte[4 * 2 * 3], "test-renderer"))
                }));
                recorder.RecordSensorOutput(new SensorOutputSet("test-run", 14, 1.4, new ISensorFrame[]
                {
                    Frame(timedOut, SensorFrameStatus.Unavailable, new ImageFrame(timedOut, default, null, null))
                }));
                recorder.Close();
            }

            string[] frames = File.ReadAllLines(Path.Combine(_root, "test-run_0", "sensors.jsonl"));
            Assert.That(frames.Length, Is.EqualTo(2));
            Assert.That(Field(frames[0], "state_sequence"), Is.EqualTo("10"));
            Assert.That(Field(frames[0], "status"), Is.EqualTo("\"Valid\""));
            Assert.That(Field(frames[0], "exposure_start_simulation_time_s"), Is.EqualTo("1"));
            Assert.That(Field(frames[0], "exposure_duration_s"), Is.EqualTo("0.01"));
            Assert.That(frames[0], Does.Contain("\"sun_position_ecef_m\":[147000000000,"));
            Assert.That(Field(frames[1], "state_sequence"), Is.EqualTo("11"));
            Assert.That(Field(frames[1], "status"), Is.EqualTo("\"Unavailable\""));
            Assert.That(Field(frames[1], "payload"), Is.EqualTo("null"));
        }

        [Test]
        public void AnUnwritableRootFailsWhenTheRecorderIsCreated()
        {
            Directory.CreateDirectory(_root);
            string blocker = Path.Combine(_root, "file");
            File.WriteAllText(blocker, "");
            Assert.Throws<IOException>(() => new RunRecorder(Path.Combine(blocker, "datasets"), "test"));
        }

        private static SimulationState State(string runId)
        {
            AnalyticSimulationEngine engine = Analytic();
            engine.Initialize(new SimulationConfiguration(runId, DateTimeOffset.UnixEpoch, 0.5));
            engine.TryReset(out SimulationState state);
            return state;
        }

        private static AnalyticSimulationEngine Analytic() => new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0);

        // The analytic engine with one ideal body-rate sensor, recorded through the gateway's events
        // as headless/Host does.
        private static SimulationGateway RecordedGateway(RunRecorder recorder)
        {
            AnalyticSimulationEngine engine = Analytic();
            return RecordedGateway(recorder, engine, engine.Orbit);
        }

        private static SimulationGateway RecordedGateway(RunRecorder recorder, ISimulationEngine engine, AnalyticOrbit orbit)
        {
            SimulationGateway gateway = new SimulationGateway(
                engine,
                sensor => new IdealBodyRateSensorModel(
                    sensor.Definition.SensorId,
                    sensor.Definition.FrameId,
                    sensor.Definition.SamplePeriodSeconds));
            gateway.RunStarted += (run, sensors) => recorder.BeginRun(run, sensors, analyticOrbit: orbit);
            gateway.StateProduced += recorder.RecordState;
            gateway.SensorOutputProduced += recorder.RecordSensorOutput;
            gateway.RunTerminated += recorder.RecordTermination;
            return gateway;
        }

        private static SimulationConfiguration Scenario(double fixedStepSeconds)
        {
            Vector3d zero = new Vector3d(0.0, 0.0, 0.0);
            Vector3d unitScale = new Vector3d(1.0, 1.0, 1.0);
            SensorConfiguration rate = SensorConfiguration.ForImu(
                new SensorDefinition("imu.body_rate", "test-rate", "body", fixedStepSeconds, "test", SensorMount.Identity),
                new ImuProfile(
                    new GyroscopeProfile(1.2e-4, 0.0, 0.0, zero, unitScale, 4.363, 1.33e-4),
                    new AccelerometerProfile(1.8e-3, 0.0, 0.0, zero, unitScale, 19.61, 6.0e-4)));
            return new SimulationConfiguration("test-run", DateTimeOffset.UnixEpoch, fixedStepSeconds, sensors: new[] { rate });
        }

        // The raw JSON value of the first "name" key in a line.
        private static string Field(string json, string name)
        {
            Match match = Regex.Match(json, "\"" + Regex.Escape(name) + "\":(\"[^\"]*\"|[^,}\\]]+)");
            Assert.That(match.Success, Is.True, $"no \"{name}\" in {json}");
            return match.Groups[1].Value;
        }
    }
}
