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
            AnalyticStateSource source = simulation.AddComponent<AnalyticStateSource>();
            SimulationRunner runner = simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            NavigationEpisodeExporter exporter = simulation.AddComponent<NavigationEpisodeExporter>();

            GameObject spacecraft = new GameObject("CubeSat Truth Pose");
            spacecraft.transform.SetParent(geospatialWorld.transform, false);
            spacecraft.AddComponent<CesiumGlobeAnchor>();

            GameObject dashboardObject = new GameObject("Argus Simulator Dashboard Test");
            dashboardObject.AddComponent<SimulatorDashboard>();

            Assert.That(runner.StepOnce(), Is.True);
            yield return null;

            CubeSatCameraRig cameraRig = spacecraft.GetComponent<CubeSatCameraRig>();
            SimulationSensorRuntime sensors = simulation.GetComponent<SimulationSensorRuntime>();

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
            Assert.That(sensors, Is.Not.Null);
            Assert.That(sensors.HasOutput, Is.True);
            Assert.That(
                sensors.Latest.TryGetFrame(
                    SimulationSensorRuntime.BodyRateSensorId,
                    out SensorFrame<AngularRateMeasurement> bodyRate),
                Is.True);
            Assert.That(bodyRate.Status, Is.EqualTo(SensorFrameStatus.Valid));
            Assert.That(GameObject.Find("Mission Control UI"), Is.Not.Null);
            Assert.That(GameObject.Find("Capture Button"), Is.Not.Null);
            Text telemetry = GameObject.Find("Telemetry Text").GetComponent<Text>();
            StringAssert.Contains("BODY-RATE SENSOR", telemetry.text);
            StringAssert.Contains("UNAVAILABLE — no model registered", telemetry.text);
            StringAssert.DoesNotContain("GPS fix", telemetry.text);
            StringAssert.DoesNotContain("mock", telemetry.text.ToLowerInvariant());
            Assert.That(exporter.IsCaptureInProgress, Is.False);
            Assert.That(exporter.EpisodeDirectory, Is.Empty);
            Assert.That(Object.FindAnyObjectByType<OrbitTrailRenderer>(), Is.Not.Null);

            Object.Destroy(dashboardObject);
            Object.Destroy(spacecraft);
            Object.Destroy(simulation);
            Object.Destroy(mainCameraObject);
            Object.Destroy(geospatialWorld);
            yield return null;

            LogAssert.NoUnexpectedReceived();
        }
    }
}
