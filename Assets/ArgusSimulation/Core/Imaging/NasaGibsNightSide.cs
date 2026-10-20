using System;

namespace Argus.Simulation.Core
{
    // Where the GIBS night-lights band goes. The longitude is read in Earth-fixed (ECEF/ITRF93)
    // axes, never in a local East-Up-North frame, whose axes are only Earth-aligned at its origin (D4).
    public static class NasaGibsNightSide
    {
        // Longitude in degrees east, in [-180, 180), of the antisolar point: the centre of the night
        // side. NaN when the direction is not finite or the Sun is over a pole, where every
        // longitude is equally lit.
        public static double AntisolarLongitudeDegrees(Vector3d sunDirectionEcef)
        {
            if (!sunDirectionEcef.IsFinite)
            {
                return double.NaN;
            }

            double equatorial = Math.Sqrt(sunDirectionEcef.X * sunDirectionEcef.X + sunDirectionEcef.Y * sunDirectionEcef.Y);
            if (equatorial <= 1e-12 * sunDirectionEcef.Magnitude)
            {
                return double.NaN;
            }

            double subsolarLongitude = Math.Atan2(sunDirectionEcef.Y, sunDirectionEcef.X) * (180.0 / Math.PI);
            return NormalizeLongitudeDegrees(subsolarLongitude + 180.0);
        }

        // Wraps any finite longitude into [-180, 180).
        public static double NormalizeLongitudeDegrees(double longitudeDegrees)
        {
            double wrapped = (longitudeDegrees + 180.0) % 360.0;
            if (wrapped < 0.0)
            {
                wrapped += 360.0;
            }

            return wrapped - 180.0;
        }
    }
}
