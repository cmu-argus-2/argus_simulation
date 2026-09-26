using System;

namespace Argus.Simulation.Core
{
    // Environment geometry source. SPICE reference files, a live SPICE service, or Basilisk
    // can implement this; dynamics backends consume it without knowing the source.
    public interface IEphemerisProvider
    {
        string SourceName { get; }

        // Simulation time 0 corresponds to this instant.
        DateTimeOffset EpochUtc { get; }

        double CoverageStartSeconds { get; }
        double CoverageEndSeconds { get; }

        // Returns false outside coverage. Never extrapolates.
        bool TryGetSample(double simulationTimeSeconds, out EphemerisSample sample);
    }
}
