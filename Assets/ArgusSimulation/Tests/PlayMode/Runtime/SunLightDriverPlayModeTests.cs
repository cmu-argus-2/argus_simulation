using System;
using System.Collections;
using System.Reflection;
using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using CesiumForUnity;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Argus.Simulation.Tests
{
    public sealed class SunLightDriverPlayModeTests
    {
        [UnityTest]
        public IEnumerator KnownSunTracksOriginChangesAndResetRestoresFallback()
        {
            var root = new GameObject("Sun test");
            var frame = root.AddComponent<CesiumGeoreference>();
            frame.SetOriginLongitudeLatitudeHeight(0, 0, 0);
            var runner = root.AddComponent<SimulationRunner>();
            var fallback = Quaternion.Euler(30, -35, 0);
            var driver = root.AddComponent<SunLightDriver>();
            var spacecraft = new SpacecraftState(0, 0, DateTimeOffset.UtcNow,
                new Vector3d(7000000, 0, 0), new Vector3d(0, 0, 0), new Quaterniond(0, 0, 0, 1),
                new Vector3d(0, 0, 0), ReferenceFrame.Itrf93);
            var environment = new EnvironmentState(new Vector3d(1.5e11, 0, 0),
                new Quaterniond(0, 0, 0, 1), new Vector3d(0, 0, 0), 1);
            typeof(SimulationRunner).GetMethod("HandleState", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(runner, new object[] { new SimulationState("sun", "test", spacecraft, null, environment) });
            // Keep the georeference transform independent of the light's rotation.
            var lightObject = new GameObject("Light");
            var separateLight = lightObject.AddComponent<Light>();
            separateLight.transform.rotation = fallback;
            var raster = root.AddComponent<NasaGibsRasterController>();
            raster.enabled = false;
            driver.Configure(runner, frame, separateLight, raster);
            root.transform.rotation = Quaternion.identity;
            yield return null;
            yield return null;
            Assert.That(driver.HasPhysicalLighting, Is.True);
            Assert.That(Vector3.Dot(-separateLight.transform.forward, Vector3.up), Is.GreaterThan(0.999f));
            frame.SetOriginLongitudeLatitudeHeight(90, 0, 0);
            yield return null;
            yield return null;
            Assert.That(Vector3.Dot(-separateLight.transform.forward, Vector3.left), Is.GreaterThan(0.999f));
            var analytic = new SpacecraftState(1, 1, DateTimeOffset.UtcNow,
                new Vector3d(7000000, 0, 0), new Vector3d(0, 0, 0), new Quaterniond(0, 0, 0, 1),
                new Vector3d(0, 0, 0), ReferenceFrame.AnalyticEarthFixed);
            typeof(SimulationRunner).GetMethod("HandleState", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(runner, new object[] { new SimulationState("analytic", "test", analytic, null) });
            yield return null;
            yield return null;
            Assert.That(driver.HasPhysicalLighting, Is.False);
            Assert.That(Quaternion.Angle(separateLight.transform.rotation, fallback), Is.LessThan(0.01f));
            Assert.That((bool)typeof(NasaGibsRasterController)
                .GetField("_hasSunDirectionInput", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(raster), Is.False);
            typeof(SimulationRunner).GetMethod("HandleHistoryReset", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(runner, null);
            yield return null;
            yield return null;
            Assert.That(driver.HasPhysicalLighting, Is.False);
            Assert.That(Quaternion.Angle(separateLight.transform.rotation, fallback), Is.LessThan(0.01f));
            UnityEngine.Object.Destroy(lightObject);
            UnityEngine.Object.Destroy(root);
            yield return null;
        }

        // A rotated georeference object: the light still points from the ITRF93 Sun, and the night
        // side's world-to-Earth-fixed conversion (NasaGibsRasterController) gets the same Sun back.
        [UnityTest]
        public IEnumerator SunFollowsARotatedGeoreference()
        {
            var root = new GameObject("Sun test");
            var frame = root.AddComponent<CesiumGeoreference>();
            frame.SetOriginLongitudeLatitudeHeight(0, 0, 0);
            root.transform.rotation = Quaternion.Euler(40, 25, -60);
            var runner = root.AddComponent<SimulationRunner>();
            var driver = root.AddComponent<SunLightDriver>();
            var spacecraft = new SpacecraftState(0, 0, DateTimeOffset.UtcNow,
                new Vector3d(7000000, 0, 0), new Vector3d(0, 0, 0), new Quaterniond(0, 0, 0, 1),
                new Vector3d(0, 0, 0), ReferenceFrame.Itrf93);
            var environment = new EnvironmentState(new Vector3d(1.5e11, 0, 0),
                new Quaterniond(0, 0, 0, 1), new Vector3d(0, 0, 0), 1);
            typeof(SimulationRunner).GetMethod("HandleState", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(runner, new object[] { new SimulationState("sun", "test", spacecraft, null, environment) });
            var lightObject = new GameObject("Light");
            var light = lightObject.AddComponent<Light>();
            var raster = root.AddComponent<NasaGibsRasterController>();
            raster.enabled = false;
            driver.Configure(runner, frame, light, raster);
            yield return null;
            yield return null;

            Vector3 towardSun = -light.transform.forward;
            // At (0, 0, 0) the Sun on ECEF +x is straight up the georeference's local axes.
            Assert.That(Vector3.Dot(towardSun, root.transform.up), Is.GreaterThan(0.999f));
            Vector3 local = root.transform.InverseTransformDirection(towardSun);
            double3 ecef = frame.TransformUnityDirectionToEarthCenteredEarthFixed(new double3(local.x, local.y, local.z));
            Assert.That(ecef.x / Math.Sqrt(ecef.x * ecef.x + ecef.y * ecef.y + ecef.z * ecef.z), Is.GreaterThan(0.999));
            UnityEngine.Object.Destroy(lightObject);
            UnityEngine.Object.Destroy(root);
            yield return null;
        }
    }
}
