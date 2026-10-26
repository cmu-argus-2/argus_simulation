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

        // The analytic engine with one ideal body-rate sensor, recorded through the gateway's events
        // as headless/Host does.
        private static SimulationGateway RecordedGateway(RunRecorder recorder)
        {
            AnalyticSimulationEngine engine = new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0);
            SimulationGateway gateway = new SimulationGateway(
                engine,
                sensor => new IdealBodyRateSensorModel(
                    sensor.Definition.SensorId,
                    sensor.Definition.FrameId,
                    sensor.Definition.SamplePeriodSeconds));
            gateway.RunStarted += (run, sensors) => recorder.BeginRun(run, sensors, analyticOrbit: engine.Orbit);
            gateway.StateProduced += recorder.RecordState;
            gateway.SensorOutputProduced += recorder.RecordSensorOutput;
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
