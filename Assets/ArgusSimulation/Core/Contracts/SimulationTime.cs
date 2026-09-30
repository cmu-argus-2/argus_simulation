using System;

namespace Argus.Simulation.Core
{
    // Simulation times are doubles derived from integer step counts (sequence x step, or Basilisk
    // nanoseconds), so two derivations of one instant differ by a few ulps of t. A fixed 1 ns
    // tolerance fails once one ulp exceeds it (t > 2^23 s, about 97 days); the relative 1e-14 term
    // covers the rounding with margin and stays far below any step (1e-5 s at t = 1e9 s).
    public static class SimulationTime
    {
        private const double AbsoluteToleranceSeconds = 1e-9;
        private const double RelativeTolerance = 1e-14;

        public static bool AreSame(double firstSeconds, double secondSeconds) =>
            Math.Abs(firstSeconds - secondSeconds) <=
            AbsoluteToleranceSeconds + RelativeTolerance * Math.Max(Math.Abs(firstSeconds), Math.Abs(secondSeconds));
    }
}
