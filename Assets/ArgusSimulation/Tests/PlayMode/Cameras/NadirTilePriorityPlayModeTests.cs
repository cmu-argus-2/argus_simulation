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
            NadirScene scene = new NadirScene();

            // One frame for Start, one for both cameras to sync their poses.
            yield return null;
            yield return null;

            Camera groundTruth = scene.CameraRig.GroundTruthCamera;
            Assert.That(scene.Priority.IsWarmingUp, Is.True);
            Assert.That(scene.Runner.IsRunning, Is.False);
            Assert.That(scene.Runner.HasState, Is.True);
            Assert.That(scene.CameraManager.useMainCamera, Is.False);
            Assert.That(scene.CameraManager.additionalCameras, Has.Member(groundTruth));
            Assert.That(scene.CameraManager.additionalCameras, Has.Member(scene.Priority.LookaheadCamera));
            Assert.That(scene.Priority.LookaheadCamera.enabled, Is.False);
            foreach (Camera camera in scene.CameraRig.Cameras)
            {
                if (camera != groundTruth)
                {
                    Assert.That(scene.CameraManager.additionalCameras, Has.No.Member(camera));
                }
            }

            // Four wall-clock seconds at 1x is roughly 30 km along the ground track.
            float leadMeters = Vector3.Distance(
                scene.Priority.LookaheadCamera.transform.position,
                groundTruth.transform.position);
            Assert.That(leadMeters, Is.InRange(20_000f, 40_000f));

            // With no tilesets in the scene, loading is complete and the gate opens.
            for (int frame = 0; frame < 60 && scene.Priority.IsWarmingUp; frame++)
            {
                yield return null;
            }

            Assert.That(scene.Priority.IsWarmingUp, Is.False);
            Assert.That(scene.Runner.IsRunning, Is.True);
            scene.AssertEveryViewRestored();

            yield return scene.Destroy();
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator ResumeDuringWarmUpSkipsTheWaitAndRestoresEveryView()
        {
            NadirScene scene = new NadirScene();
            yield return null;
            yield return null;
            Assert.That(scene.Priority.IsWarmingUp, Is.True);
            Assert.That(scene.CameraManager.useMainCamera, Is.False);

            // What the dashboard's RESUME button does.
            scene.Runner.IsRunning = true;
            yield return null;

            // The gate needs 10 stable frames to open on its own, so ending one frame after RESUME
            // means RESUME ended it.
            Assert.That(scene.Priority.IsWarmingUp, Is.False);
            Assert.That(scene.Runner.IsRunning, Is.True);
            scene.AssertEveryViewRestored();

            yield return scene.Destroy();
            LogAssert.NoUnexpectedReceived();
        }

        // A georeference with a camera manager, the main camera, an analytic runner set to run, and
        // the spacecraft with its camera rig and NadirTilePriority. No tilesets.
        private sealed class NadirScene
        {
            private readonly GameObject _geospatialWorld;
            private readonly GameObject _mainCamera;
            private readonly GameObject _simulation;
            private readonly GameObject _spacecraft;

            public NadirScene()
            {
                _geospatialWorld = new GameObject("Geospatial World");
                _geospatialWorld.AddComponent<CesiumGeoreference>();
                CameraManager = _geospatialWorld.AddComponent<CesiumCameraManager>();

                _mainCamera = new GameObject("Globe Observer");
                _mainCamera.tag = "MainCamera";
                _mainCamera.AddComponent<Camera>();

                _simulation = new GameObject("Simulation");
                AnalyticStateSource source = _simulation.AddComponent<AnalyticStateSource>();
                Runner = _simulation.AddComponent<SimulationRunner>();
                Runner.Configure(source);
                Runner.IsRunning = true;

                _spacecraft = new GameObject("CubeSat Truth Pose");
                _spacecraft.transform.SetParent(_geospatialWorld.transform, false);
                CesiumGlobeAnchor anchor = _spacecraft.AddComponent<CesiumGlobeAnchor>();
                _spacecraft.AddComponent<CesiumSpacecraftPoseDriver>().Configure(Runner, anchor);
                CameraRig = _spacecraft.AddComponent<CubeSatCameraRig>();
                Priority = _spacecraft.AddComponent<NadirTilePriority>();
                Priority.Configure(Runner);
            }

            public CesiumCameraManager CameraManager { get; }
            public SimulationRunner Runner { get; }
            public CubeSatCameraRig CameraRig { get; }
            public NadirTilePriority Priority { get; }

            public void AssertEveryViewRestored()
            {
                Assert.That(CameraManager.useMainCamera, Is.True);
                foreach (Camera camera in CameraRig.Cameras)
                {
                    Assert.That(CameraManager.additionalCameras, Has.Member(camera));
                }
            }

            public IEnumerator Destroy()
            {
                Object.Destroy(_spacecraft);
                Object.Destroy(_simulation);
                Object.Destroy(_mainCamera);
                Object.Destroy(_geospatialWorld);
                yield return null;
            }
        }
    }
}
