namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not referenced. See README.md in this folder.
    // TODO(D6): the single export route for run data.
    // - Inputs: every SimulationSnapshot, every SensorOutputSet (including ground-truth sensors
    //   and camera ImageFrames) and the gateway's command log.
    // - Output: dataset v1 as proposed in README.md (manifest, snapshots, sensors, commands, images).
    // - Passive and write-only: never feeds data back and never blocks the step loop.
    // - Add an IRunRecorder abstraction and a CommandRecord contract with it, then retire
    //   NavigationEpisodeExporter.
    internal sealed class RunRecorder
    {
    }
}
