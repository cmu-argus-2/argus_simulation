using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // The single export route for run data (D6), implemented by Core/Recording/RunRecorder. Passive
    // and write-only: it never feeds data back into the run. Calls come from the thread that owns the
    // step loop and return without waiting for the disk.
    public interface IRunRecorder : IDisposable
    {
        // Starts a new dataset directory. Call it on every gateway Reset (SimulationGateway.RunStarted),
        // before that run's state at t_0. dynamicsBackendVersion and analyticOrbit describe the engine,
        // which the gateway does not know.
        void BeginRun(
            SimulationConfiguration run,
            IReadOnlyList<SensorDefinition> sensors,
            string dynamicsBackendVersion = null,
            AnalyticOrbit? analyticOrbit = null);

        void RecordState(SimulationState state);

        // Every frame of the step, controller-visible or not.
        void RecordSensorOutput(SensorOutputSet output);

        // Why the gateway ended the run (SimulationGateway.RunTerminated); the manifest's termination.
        void RecordTermination(Observation terminal);

        // Set once writing has failed; poll it between steps to stop instead of running on.
        Exception Failure { get; }

        // Writes everything queued and the final manifest; throws if writing failed. Dispose also
        // closes, but leaves a failure to the manifest, which then stays "complete": false.
        void Close();
    }
}
