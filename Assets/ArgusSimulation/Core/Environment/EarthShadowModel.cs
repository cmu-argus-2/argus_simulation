using System;

namespace Argus.Simulation.Core
{
    public enum SunlightCondition
    {
        Sunlit,
        Penumbra,
        Umbra,
    }

    // Conical Earth-shadow model (Montenbruck & Gill, Satellite Orbits, section 3.4.2).
    // Earth and Sun are spheres; the result is the visible fraction of the Sun's disk.
    // Simplifications: spherical Earth at the equatorial radius (shadow entry/exit within a few
    // seconds of SPICE's ellipsoid for LEO), no atmospheric refraction or dimming, geometric
    // Sun position (no light-time correction).
    public static class EarthShadowModel
    {
        public const double EarthRadiusMeters = CircularOrbitModel.EarthEquatorialRadiusMeters;

        // IAU 2015 nominal solar radius.
        public const double SunRadiusMeters = 695_700_000.0;

        // Both positions are relative to Earth's center in the same frame at the same instant.
        public static double IlluminationFraction(Vector3d spacecraftPositionMeters, Vector3d sunPositionMeters)
        {
            Vector3d toSun = sunPositionMeters - spacecraftPositionMeters;
            double sunDistance = toSun.Magnitude;
            double earthDistance = spacecraftPositionMeters.Magnitude;
            if (earthDistance <= EarthRadiusMeters)
            {
                return 0.0;
            }

            double sunApparentRadius = Math.Asin(SunRadiusMeters / sunDistance);
            double earthApparentRadius = Math.Asin(EarthRadiusMeters / earthDistance);
            double cosine = Vector3d.Dot(-spacecraftPositionMeters, toSun) / (earthDistance * sunDistance);
            double separation = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosine)));

            if (separation >= sunApparentRadius + earthApparentRadius)
            {
                return 1.0;
            }

            if (separation <= earthApparentRadius - sunApparentRadius)
            {
                return 0.0;
            }

            if (separation <= sunApparentRadius - earthApparentRadius)
            {
                // Annular: Earth's disk lies entirely within the Sun's (not reachable from LEO).
                return 1.0 - (earthApparentRadius * earthApparentRadius) /
                    (sunApparentRadius * sunApparentRadius);
            }

            double a = sunApparentRadius;
            double b = earthApparentRadius;
            double x = (separation * separation + a * a - b * b) / (2.0 * separation);
            double y = Math.Sqrt(Math.Max(0.0, a * a - x * x));
            double overlap = a * a * Math.Acos(x / a) + b * b * Math.Acos((separation - x) / b) - separation * y;
            return Math.Max(0.0, Math.Min(1.0, 1.0 - overlap / (Math.PI * a * a)));
        }

        public static SunlightCondition Classify(double illuminationFraction)
        {
            if (illuminationFraction >= 1.0)
            {
                return SunlightCondition.Sunlit;
            }

            return illuminationFraction <= 0.0 ? SunlightCondition.Umbra : SunlightCondition.Penumbra;
        }
    }
}
