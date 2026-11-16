using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Host.Tests
{
    // The shipped P0 run recorded end to end without Basilisk: the analytic engine, the gateway and the
    // recorder wired as headless/Host wires them (CORE-6).
    public sealed class P0RecordingTests
    {
        [Test]
        public void P0RecordingListsTheFourBodyCamerasInDeviceOrder()
        {
            string root = Path.Combine(Path.GetTempPath(), "argus-p0-recording-" + Guid.NewGuid().ToString("N"));
            try
            {
                // P0's devices on the analytic engine, which takes no Basilisk inputs (orbit, spacecraft, kernels).
                SimulationConfiguration file = RunConfigurationFile.Load(HostOptions.DefaultRunConfigurationPath, "p0-test");
                SimulationConfiguration p0 = new SimulationConfiguration(file.RunId, file.EpochUtc, file.FixedStepSeconds, devices: file.Devices);
                using (RunRecorder recorder = new RunRecorder(root, "test"))
                {
                    SimulationGateway gateway = new SimulationGateway(new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0));
                    gateway.RunStarted += (run, sensors) => recorder.BeginRun(run, sensors);
                    gateway.StateProduced += recorder.RecordState;
                    gateway.SensorOutputProduced += recorder.RecordSensorOutput;
                    gateway.Reset(0, p0);
                    for (int step = 0; step < 150; step++)
                    {
                        gateway.Step();
                    }

                    recorder.Close();
                }

                string run = Directory.GetDirectories(root).Single();
                JsonNode manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(run, "manifest.json")));
                JsonArray cameras = manifest["cameras"].AsArray();
                Assert.That(
                    cameras.Select(camera => (string)camera["sensor_id"]),
                    Is.EqualTo(new[] { "cam.px", "cam.py", "cam.mx", "cam.my" }));
                foreach (JsonNode camera in cameras)
                {
                    string sensorId = (string)camera["sensor_id"];
                    Assert.That(camera.AsObject().Select(entry => entry.Key), Is.EqualTo(new[] { "sensor_id", "profile_id" }), sensorId);
                    Assert.That((string)camera["profile_id"], Is.EqualTo("imx708-66deg-spec-v0"), sensorId);

                    // The values are the device's parameters, under the camera kind's keys.
                    JsonNode device = manifest["devices"].AsArray().Single(d => (string)d["device_id"] == sensorId);
                    Assert.That((string)device["kind"], Is.EqualTo("camera"), sensorId);
                    Assert.That((string)device["parameters"]["profile_id"], Is.EqualTo((string)camera["profile_id"]), sensorId);
                    Assert.That((double)device["parameters"]["intrinsics.focal_length_x_px"], Is.EqualTo(3548.0), sensorId);
                    Assert.That((string)device["parameters"]["distortion.model"], Is.EqualTo("brown-conrady-5"), sensorId);
                }

                // 0.1 s steps of a 15 s camera: frames at t = 0 and 15 s, Unavailable with no renderer.
                JsonNode[] cameraFrames = File.ReadLines(Path.Combine(run, "sensors.jsonl"))
                    .Select(line => JsonNode.Parse(line))
                    .Where(frame => ((string)frame["sensor_id"]).StartsWith("cam.", StringComparison.Ordinal))
                    .ToArray();
                Assert.That(cameraFrames.Length, Is.EqualTo(8));
                Assert.That(cameraFrames.Select(frame => (string)frame["status"]), Is.All.EqualTo("Unavailable"));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }
}
