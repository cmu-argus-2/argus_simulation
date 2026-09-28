using System;
using System.Collections;
using System.Globalization;
using System.Text;
using Argus.Simulation.Unity;
using CesiumForUnity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Argus.Simulation.Tests
{
    public sealed class SimulatorDashboardPlayModeTests
    {
        private static string ReplayData(int count)
        {
            var result = new StringBuilder();
            var epoch = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
            for (int index = 0; index < count; index++)
            {
                double time = (index + 1) * 0.05;
                string timestamp = (epoch + TimeSpan.FromSeconds(time))
                    .ToString("O", CultureInfo.InvariantCulture)
                    .Replace("+00:00", "Z");
                result.Append("{\"schema\":\"basilisk-frame-replay-demo-v1\",\"frame_profile\":\"")
                    .Append(BasiliskReplayStateSource.Profile)
                    .Append("\",\"sequence\":").Append(index + 1)
                    .Append(",\"simulation_time_s\":")
                    .Append(time.ToString("R", CultureInfo.InvariantCulture))
                    .Append(",\"timestamp_utc\":\"").Append(timestamp)
                    .Append("\",\"truth\":{\"position_fixed_m\":[7000000,")
                    .Append(index).Append(",0],\"velocity_fixed_m_s\":[0,7000,0],")
                    .Append("\"body_to_fixed_xyzw\":[0,0,0,1],")
                    .Append("\"angular_velocity_body_rad_s\":[0.01,0,0]},")
                    .Append("\"measurements\":{\"gyro_body_rad_s\":[0.012,0,0]}}\n");
            }

            return result.ToString();
        }

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
            AnalyticOrbitStateSource source = simulation.AddComponent<AnalyticOrbitStateSource>();
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
            MockSensorSuite sensors = simulation.GetComponent<MockSensorSuite>();

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
            Assert.That(sensors.HasSnapshot, Is.True);
            Assert.That(GameObject.Find("Mission Control UI"), Is.Not.Null);
            Assert.That(GameObject.Find("Capture Button"), Is.Not.Null);
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

        [UnityTest]
        public IEnumerator ReplayTrailUsesEveryRecordedSample()
        {
            const int replaySampleCount = 1200;
            GameObject geospatialWorld = new GameObject("Replay Geospatial World");
            geospatialWorld.AddComponent<CesiumGeoreference>();

            GameObject simulation = new GameObject("Replay Simulation");
            BasiliskReplayStateSource source =
                simulation.AddComponent<BasiliskReplayStateSource>();
            source.LoadJsonLines(ReplayData(replaySampleCount), true);
            SimulationRunner runner = simulation.AddComponent<SimulationRunner>();
            runner.Configure(source, source.StepSeconds, source.StartTimeSeconds);

            GameObject trailObject = new GameObject("Replay Orbit Visualization");
            OrbitTrailRenderer trail = trailObject.AddComponent<OrbitTrailRenderer>();
            trail.Configure(runner);
            trail.BuildTrail();

            LineRenderer line = trailObject.GetComponent<LineRenderer>();
            Assert.That(line, Is.Not.Null);
            Assert.That(line.positionCount, Is.EqualTo(replaySampleCount));
            Assert.That(line.GetPosition(0), Is.Not.EqualTo(Vector3.zero));
            Assert.That(line.GetPosition(replaySampleCount - 1),
                Is.Not.EqualTo(line.GetPosition(0)));

            Object.Destroy(trailObject);
            Object.Destroy(simulation);
            Object.Destroy(geospatialWorld);
            yield return null;

            LogAssert.NoUnexpectedReceived();
        }
    }
}
