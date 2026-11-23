using System.Collections;
using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using CesiumForUnity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Argus.Simulation.Tests
{
    public sealed class SimulatorDashboardPlayModeTests
    {
        [UnityTest]
        public IEnumerator DashboardBuildsMissionControlAndProducesTelemetry()
        {
            GameObject geospatialWorld = new GameObject("Geospatial World");
            geospatialWorld.AddComponent<CesiumGeoreference>();
            geospatialWorld.AddComponent<CesiumCameraManager>();

            GameObject mainCameraObject = new GameObject("Globe Observer");
            mainCameraObject.tag = "MainCamera";
            mainCameraObject.AddComponent<Camera>();

            GameObject simulation = new GameObject("Simulation");
            TestStateSource source = simulation.AddComponent<TestStateSource>();
            SimulationRunner runner = simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            NavigationEpisodeExporter exporter = simulation.AddComponent<NavigationEpisodeExporter>();

            GameObject spacecraft = new GameObject("CubeSat Truth Pose");
            spacecraft.transform.SetParent(geospatialWorld.transform, false);
            spacecraft.AddComponent<CesiumGlobeAnchor>();

            GameObject dashboardObject = new GameObject("Argus Simulator Dashboard Test");
            dashboardObject.AddComponent<SimulatorDashboard>();

            source.Publish(0);
            source.PublishOutput(new SensorOutputSet("test-run", 0, 0.0, new ISensorFrame[]
            {
                Frame("imu-0", SensorFrameStatus.Valid, new ImuMeasurement(new Vector3d(0.01, 0.0, 0.0), new Vector3d(0.0, 0.0, 0.0))),
                Frame("magnetometer-0", SensorFrameStatus.Stale, new MagnetometerMeasurement(new Vector3d(2e-5, 0.0, -3e-5))),
                Frame("light-0", SensorFrameStatus.Unavailable, default(LightSensorMeasurement))
            }));
            yield return null;

            CubeSatCameraRig cameraRig = spacecraft.GetComponent<CubeSatCameraRig>();

            Assert.That(spacecraft.transform.Find("CubeSat Visual Model"), Is.Not.Null);
            Assert.That(spacecraft.transform.Find("CubeSat Visual Model/1U Chassis"), Is.Not.Null);
            Assert.That(cameraRig, Is.Not.Null);
            Assert.That(cameraRig.RenderTextures.Count, Is.EqualTo(5));
            Assert.That(cameraRig.Names[0], Is.EqualTo("FORWARD  +X"));
            Assert.That(cameraRig.Names[1], Is.EqualTo("AFT  -X"));
            Assert.That(cameraRig.Names[2], Is.EqualTo("STARBOARD  +Y"));
            Assert.That(cameraRig.Names[3], Is.EqualTo("PORT  -Y"));
            Assert.That(cameraRig.Names[4], Is.EqualTo("NADIR GT  NORTH-UP"));
            Assert.That(cameraRig.GroundTruthCamera, Is.Not.Null);
            Assert.That(cameraRig.GroundTruthCamera.transform.IsChildOf(spacecraft.transform), Is.False);
            Assert.That(GameObject.Find("Mission Control UI"), Is.Not.Null);
            Assert.That(GameObject.Find("Capture Button"), Is.Not.Null);
            // The host owns the run: no control that would step, pause or nudge a local one.
            Assert.That(GameObject.Find("Pause Button"), Is.Null);
            Assert.That(GameObject.Find("Speed Button"), Is.Null);
            Assert.That(GameObject.Find("Pose Controls"), Is.Null);
            Assert.That(GameObject.Find("Stream Status"), Is.Not.Null);
            Text telemetry = GameObject.Find("Telemetry Text").GetComponent<Text>();
            StringAssert.Contains("Non-physical lighting", telemetry.text);
            StringAssert.Contains("SPACECRAFT TRUTH", telemetry.text);
            StringAssert.Contains("HOST SENSORS", telemetry.text);
            // Every streamed sensor with its status; only frames with a reading show one.
            StringAssert.Contains("imu-0:</color>  <color=#4DFF94>VALID", telemetry.text);
            StringAssert.Contains("magnetometer-0:</color>  <color=#FAB838>STALE", telemetry.text);
            StringAssert.Contains("light-0:</color>  <color=#FF7366>UNAVAILABLE", telemetry.text);
            StringAssert.Contains("ω rad/s", telemetry.text);
            StringAssert.Contains("B µT", telemetry.text);
            StringAssert.DoesNotContain("E W/m²", telemetry.text);
            StringAssert.DoesNotContain("no model registered", telemetry.text);
            StringAssert.DoesNotContain("GPS fix", telemetry.text);
            StringAssert.DoesNotContain("mock", telemetry.text.ToLowerInvariant());
            Assert.That(exporter.IsCaptureInProgress, Is.False);
            Assert.That(exporter.EpisodeDirectory, Is.Empty);
            OrbitTrailRenderer trail = Object.FindAnyObjectByType<OrbitTrailRenderer>();
            Assert.That(trail, Is.Not.Null);
            // One point per 10 simulated seconds: step 1 (0.1 s) adds none, step 100 (10 s) one.
            source.Publish(1);
            Assert.That(trail.PointCount, Is.EqualTo(1));
            source.Publish(100);
            Assert.That(trail.PointCount, Is.EqualTo(2));
            source.ResetHistory();
            Assert.That(trail.PointCount, Is.Zero);

            Object.Destroy(dashboardObject);
            Object.Destroy(spacecraft);
            Object.Destroy(simulation);
            Object.Destroy(mainCameraObject);
            Object.Destroy(geospatialWorld);
            yield return null;

            LogAssert.NoUnexpectedReceived();
        }

        private static ISensorFrame Frame<T>(string sensorId, SensorFrameStatus status, T payload) =>
            new SensorFrame<T>("test-run", sensorId, sensorId + "/0", 0, 0, 0.0, System.DateTimeOffset.UnixEpoch, status, "test", payload);
    }
}
