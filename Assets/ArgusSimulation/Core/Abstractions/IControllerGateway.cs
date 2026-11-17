using System;

namespace Argus.Simulation.Core
{
    // The one controller interface (CORE-9, G1, D7): headless controllers, tests, Unity and HIL use it.
    // SimulationGateway implements it in process; the gateway link (CON-3 #35, HOST-6 #30) carries the
    // same calls. One controller holds actuator authority at a time. Its accepted calls (a claim, a heartbeat, an
    // accepted command) are its heartbeat, and
    // a missed heartbeat past the run's timeout fails the run safe. Which calls step the run depends on
    // the gateway's StepDriver: when its owner steps it (paced and HIL runs), the controller submits
    // each step's command with TrySubmit and the owner's Step() applies it; when the controller steps it
    // (lockstep), it calls Step. In lockstep, wall time between calls is silence: the run's timeout is
    // longer than the controller's slowest step, or a controller that can call while it plans sends
    // heartbeats then. The owner's own calls (Reset, Step(), Stop, Finish, CheckHeartbeat,
    // Revoke) are not part of it. Single-threaded: call it from the thread that runs the gateway, never
    // from inside one of its events.
    public interface IControllerGateway
    {
        // The step k a command must name, and its time t_k.
        long NextSequence { get; }
        double NextSimulationTimeSeconds { get; }

        // Every controller-visible observation, once its step is committed: observation 0, each step's,
        // and the terminal one. A terminal observation's reason can carry a backend failure's message,
        // as the owner's Step return always has; the gateway link (HOST-6) sends controllers its status
        // only.
        event Action<Observation> ObservationPublished;

        // The run ended (stopped, failed, finished, or replaced by a Reset); controllers stop here.
        // Only a stop or a failure publishes a terminal observation first.
        event Action RunEnded;

        // Each returns false with the reason when refused.
        bool TryClaim(string controllerId, out string refusal);
        bool TryRelease(string controllerId, out string refusal);
        bool TryHeartbeat(string controllerId, out string refusal);

        // StepDriver.Host: the command for the next step, which the owner's Step() applies.
        bool TrySubmit(string controllerId, DeviceCommandSet command, out string refusal);

        // StepDriver.Controller: applies command k and returns observation k+1 (target-architecture §4).
        Observation Step(string controllerId, DeviceCommandSet command);
    }
}
