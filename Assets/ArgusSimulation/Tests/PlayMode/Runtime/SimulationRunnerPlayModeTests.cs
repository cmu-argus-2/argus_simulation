using System.Collections;
using System.Collections.Generic;
using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using CesiumForUnity;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Argus.Simulation.Tests
{
    // The runner follows the host (UNITY-6): it publishes each state as delivered, never stepping or
    // re-timing it, and presentation interpolates between states without changing them.
    public sealed class SimulationRunnerPlayModeTests
    {
        [UnityTest]
        public IEnumerator TheRunnerPublishesStatesAsDeliveredAndThePoseInterpolatesBetweenThem()
        {
            GameObject world = new GameObject("Geospatial World");
            world.AddComponent<CesiumGeoreference>();
            GameObject simulation = new GameObject("Simulation");
            TestStateSource source = simulation.AddComponent<TestStateSource>();
            SimulationRunner runner = simulation.AddComponent<SimulationRunner>();
            runner.Configure(source);
            GameObject spacecraft = new GameObject("CubeSat Truth Pose");
            spacecraft.transform.SetParent(world.transform, false);
            CesiumGlobeAnchor anchor = spacecraft.AddComponent<CesiumGlobeAnchor>();
            spacecraft.AddComponent<CesiumSpacecraftPoseDriver>().Configure(runner, anchor);
            List<SimulationState> published = new List<SimulationState>();
            runner.StateProduced += published.Add;

            SimulationState first = source.Publish(0);
            yield return new WaitForSecondsRealtime(0.5f);
            // A decimated stream: 10 simulated seconds later, half a second of wall time later.
            SimulationState second = source.Publish(100);
            yield return null;

            Assert.That(published.Count, Is.EqualTo(2));
            Assert.That(published[1].Spacecraft.Sequence, Is.EqualTo(100));
            Assert.That(runner.LastState.Spacecraft.PositionEcefMeters, Is.EqualTo(second.Spacecraft.PositionEcefMeters));
            Assert.That(runner.SimulationTimeSeconds, Is.EqualTo(10.0));
            Assert.That(runner.SimulationRate, Is.GreaterThan(1.0));

            // One frame after the second state the pose has barely left the first.
            double span = Distance(first.Spacecraft.PositionEcefMeters, second.Spacecraft.PositionEcefMeters);
            Assert.That(Distance(anchor.positionGlobeFixed, first.Spacecraft.PositionEcefMeters), Is.LessThan(span * 0.5));
            yield return new WaitForSecondsRealtime(0.6f);
            Assert.That(Distance(anchor.positionGlobeFixed, second.Spacecraft.PositionEcefMeters), Is.LessThan(1.0));

            source.ResetHistory();
            Assert.That(runner.HasState, Is.False);

            Object.Destroy(spacecraft);
            Object.Destroy(simulation);
            Object.Destroy(world);
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }

        // The jump threshold follows the stream's cadence: a steady 1 s cadence glides every interval,
        // and a gap five times the cadence jumps.
        [UnityTest]
        public IEnumerator ThePoseGlidesAtAnyCadenceAndJumpsOnlyAcrossAGap()
        {
            PoseScene scene = new PoseScene();
            scene.Source.Publish(0);
            yield return new WaitForSecondsRealtime(1.0f);
            SimulationState second = scene.Source.Publish(10);
            yield return new WaitForSecondsRealtime(1.0f);
            SimulationState third = scene.Source.Publish(20);
            yield return null;
            // Just after the third state, a gliding pose has barely left the second.
            double step = Distance(second.Spacecraft.PositionEcefMeters, third.Spacecraft.PositionEcefMeters);
            Assert.That(scene.DistanceTo(second), Is.LessThan(step * 0.25));

            scene.Source.ResetHistory();
            scene.Source.Publish(0);
            yield return new WaitForSecondsRealtime(0.2f);
            scene.Source.Publish(10);
            yield return new WaitForSecondsRealtime(1.0f);
            SimulationState afterGap = scene.Source.Publish(20);
            yield return null;
            Assert.That(scene.DistanceTo(afterGap), Is.LessThan(1.0));

            yield return scene.Destroy();
            LogAssert.NoUnexpectedReceived();
        }

        // A capture holds the pose at its state while states keep arriving, then the pose follows again.
        [UnityTest]
        public IEnumerator AHeldPoseStaysAtItsStateUntilReleased()
        {
            PoseScene scene = new PoseScene();
            SimulationState held = scene.Source.Publish(0);
            yield return null;
            scene.Pose.Hold(held);
            yield return new WaitForSecondsRealtime(0.2f);
            scene.Source.Publish(10);
            yield return new WaitForSecondsRealtime(0.2f);
            scene.Source.Publish(20);
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(scene.Pose.IsHeld, Is.True);
            Assert.That(scene.DistanceTo(held), Is.LessThan(1.0));

            scene.Pose.Release();
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(scene.DistanceTo(held), Is.GreaterThan(1_000.0));

            yield return scene.Destroy();
            LogAssert.NoUnexpectedReceived();
        }

        private static double Distance(Vector3d a, Vector3d b) => (a - b).Magnitude;

        private static double Distance(double3 a, Vector3d b) => Distance(new Vector3d(a.x, a.y, a.z), b);

        // A georeference, a runner following a test source, and the CubeSat's anchor and pose driver.
        private sealed class PoseScene
        {
            private readonly GameObject _world;
            private readonly GameObject _simulation;
            private readonly GameObject _spacecraft;
            private readonly CesiumGlobeAnchor _anchor;

            public PoseScene()
            {
                _world = new GameObject("Geospatial World");
                _world.AddComponent<CesiumGeoreference>();
                _simulation = new GameObject("Simulation");
                Source = _simulation.AddComponent<TestStateSource>();
                SimulationRunner runner = _simulation.AddComponent<SimulationRunner>();
                runner.Configure(Source);
                _spacecraft = new GameObject("CubeSat Truth Pose");
                _spacecraft.transform.SetParent(_world.transform, false);
                _anchor = _spacecraft.AddComponent<CesiumGlobeAnchor>();
                Pose = _spacecraft.AddComponent<CesiumSpacecraftPoseDriver>();
                Pose.Configure(runner, _anchor);
            }

            public TestStateSource Source { get; }
            public CesiumSpacecraftPoseDriver Pose { get; }

            public double DistanceTo(SimulationState state) =>
                Distance(_anchor.positionGlobeFixed, state.Spacecraft.PositionEcefMeters);

            public IEnumerator Destroy()
            {
                Object.Destroy(_spacecraft);
                Object.Destroy(_simulation);
                Object.Destroy(_world);
                yield return null;
            }
        }
    }
}
