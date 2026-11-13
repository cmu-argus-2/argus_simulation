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
        public IEnumerator WarmUpWaitsForAStateWithEveryViewLoadingThenEnds()
        {
            NadirScene scene = new NadirScene();

            // One frame for Start. With no state from the host yet, the warm-up waits for one, and
            // every view keeps loading meanwhile, so the globe is never left blank.
            yield return null;
            yield return null;

            Assert.That(scene.Priority.IsWarmingUp, Is.True);
            scene.AssertEveryViewRestored();
            Assert.That(scene.Priority.LookaheadCamera.enabled, Is.False);

            // With a state and no tilesets in the scene, loading is complete and the gate opens.
            scene.Source.Publish(0);
            yield return scene.WaitForWarmUp();

            Assert.That(scene.Priority.IsWarmingUp, Is.False);
            Assert.That(scene.Runner.HasState, Is.True);
            scene.AssertEveryViewRestored();

            yield return scene.Destroy();
            LogAssert.NoUnexpectedReceived();
        }

        // The host owns the run, so a new run or subscription jumps the pose: the nadir view loads
        // first again, without holding anything.
        [UnityTest]
        public IEnumerator ANewRunWarmsUpTheNadirViewAgain()
        {
            NadirScene scene = new NadirScene();
            yield return null;
            scene.Source.Publish(0);
            yield return scene.WaitForWarmUp();
            Assert.That(scene.Priority.IsWarmingUp, Is.False);

            scene.Source.ResetHistory();
            yield return null;
            Assert.That(scene.Priority.IsWarmingUp, Is.True);
            Assert.That(scene.Runner.HasState, Is.False);
            scene.AssertEveryViewRestored();

            scene.Source.Publish(0);
            yield return scene.WaitForWarmUp();
            Assert.That(scene.Priority.IsWarmingUp, Is.False);
            scene.AssertEveryViewRestored();

            yield return scene.Destroy();
            LogAssert.NoUnexpectedReceived();
        }

        // A georeference with a camera manager, the main camera, a runner following a test source,
        // and the spacecraft with its camera rig and NadirTilePriority. No tilesets.
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
                Source = _simulation.AddComponent<TestStateSource>();
                Runner = _simulation.AddComponent<SimulationRunner>();
                Runner.Configure(Source);

                _spacecraft = new GameObject("CubeSat Truth Pose");
                _spacecraft.transform.SetParent(_geospatialWorld.transform, false);
                CesiumGlobeAnchor anchor = _spacecraft.AddComponent<CesiumGlobeAnchor>();
                _spacecraft.AddComponent<CesiumSpacecraftPoseDriver>().Configure(Runner, anchor);
                CameraRig = _spacecraft.AddComponent<CubeSatCameraRig>();
                Priority = _spacecraft.AddComponent<NadirTilePriority>();
                Priority.Configure(Runner);
            }

            public CesiumCameraManager CameraManager { get; }
            public TestStateSource Source { get; }
            public SimulationRunner Runner { get; }
            public CubeSatCameraRig CameraRig { get; }
            public NadirTilePriority Priority { get; }

            public IEnumerator WaitForWarmUp()
            {
                for (int frame = 0; frame < 60 && Priority.IsWarmingUp; frame++)
                {
                    yield return null;
                }
            }

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
