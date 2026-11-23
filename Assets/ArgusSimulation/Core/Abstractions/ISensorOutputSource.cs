using System;

namespace Argus.Simulation.Core
{
    // Delivers the SensorOutputSet paired with each state, right after that state's StateProduced:
    // the latest frame of each non-camera sensor as of the state (the host's stream projection).
    public interface ISensorOutputSource
    {
        event Action<SensorOutputSet> OutputProduced;
    }
}
