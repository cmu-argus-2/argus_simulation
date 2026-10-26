using System.Collections;
using Argus.Simulation.Unity;
using CesiumForUnity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Argus.Simulation.Tests
{
    public sealed class NadirTilePriorityPlayModeTests
    {
        [UnityTest]
        public IEnumerator WarmUpLoadsOnlyNadirViewsThenRestoresEveryView()
        {
            GameObject geospatialWorld = new GameObject("Geospatial World");
            geospatialWorld.AddComponent<CesiumGeoreference>();
            CesiumCameraManager cameraManager = geospatialWorld.AddComponent<CesiumCameraManager>();

            GameObject mainCameraObject = new GameObject("Globe Observer");
            mainCameraObject.tag = "MainCamera";
            mainCameraObject.AddComponent<Camera>();

            GameObject simulation = new GameObject("Simulation");
            AnalyticStateSource source = simulation.AddComponent<AnalyticStateSource>();
            SimulationRunner runner = simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            runner.IsRunning = true;

            GameObject spacecraft = new GameObject("CubeSat Truth Pose");
            spacecraft.transform.SetParent(geospatialWorld.transform, false);
            CesiumGlobeAnchor anchor = spacecraft.AddComponent<CesiumGlobeAnchor>();
            spacecraft.AddComponent<CesiumSpacecraftPoseDriver>().Configure(runner, anchor);
            CubeSatCameraRig cameraRig = spacecraft.AddComponent<CubeSatCameraRig>();
            NadirTilePriority priority = spacecraft.AddComponent<NadirTilePriority>();
            priority.Configure(runner);

            // One frame for Start, one for both cameras to sync their poses.
            yield return null;
            yield return null;

            Camera groundTruth = cameraRig.GroundTruthCamera;
            Assert.That(priority.IsWarmingUp, Is.True);
            Assert.That(runner.IsRunning, Is.False);
            Assert.That(runner.HasState, Is.True);
            Assert.That(cameraManager.useMainCamera, Is.False);
            Assert.That(cameraManager.additionalCameras, Has.Member(groundTruth));
            Assert.That(cameraManager.additionalCameras, Has.Member(priority.LookaheadCamera));
            Assert.That(priority.LookaheadCamera.enabled, Is.False);
            foreach (Camera camera in cameraRig.Cameras)
            {
                if (camera != groundTruth)
                {
                    Assert.That(cameraManager.additionalCameras, Has.No.Member(camera));
                }
            }

            // Four wall-clock seconds at 1x is roughly 30 km along the ground track.
            float leadMeters = Vector3.Distance(
                priority.LookaheadCamera.transform.position,
                groundTruth.transform.position);
            Assert.That(leadMeters, Is.InRange(20_000f, 40_000f));

            // With no tilesets in the scene, loading is complete and the gate opens.
            for (int frame = 0; frame < 60 && priority.IsWarmingUp; frame++)
            {
                yield return null;
            }

            Assert.That(priority.IsWarmingUp, Is.False);
            Assert.That(runner.IsRunning, Is.True);
            Assert.That(cameraManager.useMainCamera, Is.True);
            foreach (Camera camera in cameraRig.Cameras)
            {
                Assert.That(cameraManager.additionalCameras, Has.Member(camera));
            }

            Object.Destroy(spacecraft);
            Object.Destroy(simulation);
            Object.Destroy(mainCameraObject);
            Object.Destroy(geospatialWorld);
            yield return null;

            LogAssert.NoUnexpectedReceived();
        }
    }
}
