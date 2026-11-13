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

        private static double Distance(Vector3d a, Vector3d b) => (a - b).Magnitude;

        private static double Distance(double3 a, Vector3d b) => Distance(new Vector3d(a.x, a.y, a.z), b);
    }
}
