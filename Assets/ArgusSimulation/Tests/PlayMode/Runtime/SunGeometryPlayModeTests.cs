using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using CesiumForUnity;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Tests
{
    public sealed class SunGeometryPlayModeTests
    {
        private GameObject _world;
        private GameObject _simulation;
        private GameObject _sunlight;
        private AnalyticOrbitStateSource _source;
        private SimulationRunner _runner;
        private MockSensorSuite _sensors;
        private CesiumGlobeAnchor _anchor;
        private CesiumGeoreference _georeference;

        [SetUp]
        public void BuildPipeline()
        {
            _world = new GameObject("Geospatial World");
            _georeference = _world.AddComponent<CesiumGeoreference>();

            _simulation = new GameObject("Simulation");
            _source = _simulation.AddComponent<AnalyticOrbitStateSource>();
            _runner = _simulation.AddComponent<SimulationRunner>();
            _runner.Configure(_source);
            _runner.IsRunning = false;
            _sensors = _simulation.AddComponent<MockSensorSuite>();
            _sensors.Configure(_runner);

            GameObject spacecraft = new GameObject("CubeSat Truth Pose");
            spacecraft.transform.SetParent(_world.transform, false);
            _anchor = spacecraft.AddComponent<CesiumGlobeAnchor>();
            spacecraft.AddComponent<CesiumSpacecraftPoseDriver>().Configure(_runner, _anchor);

            _sunlight = new GameObject("Sunlight");
            _sunlight.AddComponent<Light>().type = LightType.Directional;
            _sunlight.AddComponent<SunLightDriver>().Configure(_runner);
        }

        [TearDown]
        public void DestroyPipeline()
        {
            // Immediate, so FindAnyObjectByType in the next test cannot see this test's objects.
            Object.DestroyImmediate(_sunlight);
            Object.DestroyImmediate(_simulation);
            Object.DestroyImmediate(_world);
        }

        [Test]
        public void AttitudeNudge_UpdatesRenderedPoseAndSunSensorConsistently()
        {
            Assert.That(_runner.StepOnce(), Is.True);
            Vector3d before = _sensors.Latest.SunVectorBody;
            Assert.That(_sensors.Latest.HasSunGeometry, Is.True);

            // Unity Euler (0, 0, 90) is a +90 deg rotation about body +Z.
            _source.NudgeAttitude(new Vector3(0f, 0f, 90f));
            Assert.That(_runner.StepOnce(), Is.True);
            SpacecraftState state = _runner.LastState;
            Vector3d after = _sensors.Latest.SunVectorBody;

            // 0.1 s of nadir-tracking motion moves the Sun by ~1e-4 rad in the body frame.
            Assert.That((after - new Vector3d(before.Y, -before.X, before.Z)).Magnitude, Is.LessThan(1e-3));
            Quaterniond sensed = _sensors.Latest.State.BodyToEcef;
            Assert.That(
                new[] { sensed.X, sensed.Y, sensed.Z, sensed.W },
                Is.EqualTo(new[] { state.BodyToEcef.X, state.BodyToEcef.Y, state.BodyToEcef.Z, state.BodyToEcef.W }),
                "sensors use the shared attitude");
            Assert.That(state.BodyToEcef.Magnitude, Is.EqualTo(1.0).Within(1e-12));

            quaternion rendered = _anchor.rotationGlobeFixed;
            Quaterniond renderedRotation = new Quaterniond(rendered.value.x, rendered.value.y, rendered.value.z, rendered.value.w);
            Assert.That(SameRotation(renderedRotation, state.BodyToEcef, 1e-5), Is.True, "rendered pose uses the shared attitude");

            _source.ResetManualAttitude();
            Assert.That(_source.ManualAttitudeOffsetDegrees, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void Sensors_ReportUmbraDuringEclipseAndSunlightAfter()
        {
            _runner.Configure(_source, 100.0);
            _runner.IsRunning = false;

            Assert.That(_runner.StepOnce(), Is.True);
            Assert.That(_sensors.Latest.SunlightCondition, Is.EqualTo(SunlightCondition.Sunlit));

            while (_runner.SimulationTimeSeconds <= 1_800.0)
            {
                Assert.That(_runner.StepOnce(), Is.True);
            }

            Assert.That(_runner.LastState.SimulationTimeSeconds, Is.EqualTo(1_800.0).Within(1e-6));
            Assert.That(_sensors.Latest.SunlightCondition, Is.EqualTo(SunlightCondition.Umbra));
            Assert.That(_sensors.Latest.SunVectorBody.Magnitude, Is.EqualTo(0.0));
            Assert.That(_sensors.Latest.SolarPowerWatts, Is.EqualTo(0.0));

            while (_runner.SimulationTimeSeconds <= 4_000.0)
            {
                Assert.That(_runner.StepOnce(), Is.True);
            }

            Assert.That(_sensors.Latest.SunlightCondition, Is.EqualTo(SunlightCondition.Sunlit));
        }

        [Test]
        public void SunLight_PointsAlongSpiceSunDirection()
        {
            Assert.That(_runner.StepOnce(), Is.True);
            SunLightDriver driver = _sunlight.GetComponent<SunLightDriver>();
            Assert.That(driver.HasSunDirection, Is.True);

            Vector3 toSun = -_sunlight.transform.forward;
            double3 ecef = _georeference.TransformUnityDirectionToEarthCenteredEarthFixed(
                new double3(toSun.x, toSun.y, toSun.z));
            Vector3d lightDirection = new Vector3d(ecef.x, ecef.y, ecef.z).Normalized();

            Assert.That((lightDirection - driver.SunDirectionItrf93).Magnitude, Is.LessThan(1e-5));
        }

        // q and -q are the same rotation.
        private static bool SameRotation(Quaterniond a, Quaterniond b, double tolerance)
        {
            double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            return System.Math.Abs(System.Math.Abs(dot) - 1.0) <= tolerance;
        }
    }
}
