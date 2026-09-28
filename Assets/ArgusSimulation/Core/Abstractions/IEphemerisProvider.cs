using System;

namespace Argus.Simulation.Core
{
    // Live environment-geometry source. A SPICE runtime or Basilisk can implement this;
    // dynamics backends consume it without knowing the source.
    public interface IEphemerisProvider
    {
        string SourceName { get; }

        // Simulation time 0 corresponds to this instant.
        DateTimeOffset EpochUtc { get; }

        // Queries the environment at the requested simulation instant. Returns false when
        // the runtime cannot provide an exact result; implementations must not replay or
        // extrapolate a pre-generated scenario dataset.
        bool TryGetSample(double simulationTimeSeconds, out EphemerisSample sample);
    }
}
