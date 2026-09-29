using System;

namespace Argus.Simulation.Core
{
    // Basilisk simulation time is an integer nanosecond count from the run epoch.
    internal static class BasiliskTime
    {
        private const long NanosecondsPerSecond = 1_000_000_000;
        private const double ExactnessToleranceSeconds = 1e-12;

        // Throws unless the value is a non-negative, whole number of nanoseconds.
        public static long ToNanoseconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
            {
                throw new ArgumentException("Time must be finite and non-negative.", nameof(seconds));
            }

            double nanoseconds = Math.Round(seconds * NanosecondsPerSecond);
            if (nanoseconds >= long.MaxValue)
            {
                throw new ArgumentException("Time overflows Basilisk's nanosecond clock.", nameof(seconds));
            }

            if (Math.Abs(nanoseconds / NanosecondsPerSecond - seconds) > ExactnessToleranceSeconds ||
                (seconds > 0.0 && nanoseconds == 0.0))
            {
                throw new ArgumentException(
                    $"{seconds:R} s is not a whole number of nanoseconds.",
                    nameof(seconds));
            }

            return (long)nanoseconds;
        }

        // Splits whole seconds from the remainder to keep full precision on long runs.
        public static double ToSeconds(long nanoseconds) =>
            nanoseconds / NanosecondsPerSecond + (nanoseconds % NanosecondsPerSecond) * 1e-9;

        // Epoch plus elapsed ephemeris-time seconds, as Basilisk's SPICE time is ETInit + t: no leap
        // seconds, and up to about 1.7 ms of periodic TDB-UTC error. Truncates to 100 ns ticks.
        public static DateTimeOffset ToUtc(DateTimeOffset epochUtc, long nanoseconds) =>
            epochUtc.AddTicks(nanoseconds / 100);
    }
}
