using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class NasaGibsNightSideTests
    {
        // Foundation.unity: CesiumGeoreference origin and the fixed Sunlight rotation from
        // FoundationSceneBuilder (Quaternion.Euler(30, -35, 0)).
        private const double OriginLatitudeDegrees = 39.736401;
        private const double OriginLongitudeDegrees = -105.25737;
        private const double LightPitchDegrees = 30.0;
        private const double LightYawDegrees = -35.0;

        [Test]
        public void AntisolarLongitudeDegrees_IsOppositeTheSubsolarLongitude()
        {
            Assert.That(NasaGibsNightSide.AntisolarLongitudeDegrees(new Vector3d(1.0, 0.0, 0.0)), Is.EqualTo(-180.0));
            Assert.That(NasaGibsNightSide.AntisolarLongitudeDegrees(new Vector3d(0.0, 1.0, 0.0)), Is.EqualTo(-90.0).Within(1e-12));
            Assert.That(NasaGibsNightSide.AntisolarLongitudeDegrees(new Vector3d(-2.0, 0.0, 0.5)), Is.EqualTo(0.0).Within(1e-12));
            Assert.That(NasaGibsNightSide.AntisolarLongitudeDegrees(new Vector3d(0.0, -3.0, -1.0)), Is.EqualTo(90.0).Within(1e-12));
        }

        [Test]
        public void AntisolarLongitudeDegrees_IsUndefinedOverAPoleOrForNonFiniteInput()
        {
            Assert.That(NasaGibsNightSide.AntisolarLongitudeDegrees(new Vector3d(0.0, 0.0, 1.0)), Is.NaN);
            Assert.That(NasaGibsNightSide.AntisolarLongitudeDegrees(new Vector3d(0.0, 0.0, 0.0)), Is.NaN);
            Assert.That(NasaGibsNightSide.AntisolarLongitudeDegrees(new Vector3d(double.NaN, 1.0, 0.0)), Is.NaN);
        }

        [Test]
        public void AntisolarLongitudeDegrees_ForTheFixedLightNearDenver_IsEarthFixedNotLocalEastNorth()
        {
            // The Unity georeference frame is x = East, y = Up, z = North at the origin.
            double pitch = LightPitchDegrees * Math.PI / 180.0;
            double yaw = LightYawDegrees * Math.PI / 180.0;
            Vector3d towardSunLocal = new Vector3d(
                -Math.Sin(yaw) * Math.Cos(pitch),
                Math.Sin(pitch),
                -Math.Cos(yaw) * Math.Cos(pitch));
            Vector3d towardSunEcef = LocalEastUpNorthToEcef(towardSunLocal);

            double nightCenter = NasaGibsNightSide.AntisolarLongitudeDegrees(towardSunEcef);

            // The light's subsolar point is at about 13.06 S, 74.60 W.
            Assert.That(nightCenter, Is.EqualTo(105.400655).Within(1e-5));

            // The old reading, atan2(north, east) in the local frame, put the band at 125 E.
            double localReading = NasaGibsNightSide.NormalizeLongitudeDegrees(
                Math.Atan2(towardSunLocal.Z, towardSunLocal.X) * 180.0 / Math.PI + 180.0);
            Assert.That(localReading, Is.EqualTo(125.0).Within(1e-9));
            Assert.That(localReading - nightCenter, Is.EqualTo(19.599345).Within(1e-5));
        }

        [Test]
        public void NormalizeLongitudeDegrees_WrapsIntoHalfOpenRange()
        {
            Assert.That(NasaGibsNightSide.NormalizeLongitudeDegrees(180.0), Is.EqualTo(-180.0));
            Assert.That(NasaGibsNightSide.NormalizeLongitudeDegrees(540.0), Is.EqualTo(-180.0));
            Assert.That(NasaGibsNightSide.NormalizeLongitudeDegrees(-190.0), Is.EqualTo(170.0).Within(1e-12));
            Assert.That(NasaGibsNightSide.NormalizeLongitudeDegrees(179.5), Is.EqualTo(179.5).Within(1e-12));
        }

        private static Vector3d LocalEastUpNorthToEcef(Vector3d local)
        {
            double latitude = OriginLatitudeDegrees * Math.PI / 180.0;
            double longitude = OriginLongitudeDegrees * Math.PI / 180.0;
            Vector3d east = new Vector3d(-Math.Sin(longitude), Math.Cos(longitude), 0.0);
            Vector3d north = new Vector3d(
                -Math.Sin(latitude) * Math.Cos(longitude),
                -Math.Sin(latitude) * Math.Sin(longitude),
                Math.Cos(latitude));
            Vector3d up = new Vector3d(
                Math.Cos(latitude) * Math.Cos(longitude),
                Math.Cos(latitude) * Math.Sin(longitude),
                Math.Sin(latitude));
            return east * local.X + up * local.Y + north * local.Z;
        }
    }
}
