namespace Argus.Simulation.Host
{
    // PLACEHOLDER: not implemented, not referenced.
    // TODO(D7, G1): HOST-6, the controller link for HIL flight computers (Argus.Hardware) and other
    // external controllers over argus/gateway/v1, serving SimulationGateway's IControllerGateway
    // (CORE-9: authority, heartbeat, failsafe; CORE-10: limits). Observations out
    // (ObservationPublished: controller-visible frames only, never truth), RunEnded, and claim,
    // release, heartbeat and commands in. Pass every command that decodes into a DeviceCommandSet to
    // TrySubmit or Step(controllerId, …), ones the gateway refuses included, so it reaches the command
    // log (CommandLogged) and the recorder (D6); a wire command that doesn't decode gets a refused
    // CallResult naming the field and is not logged (CON-3). Its rules: the server assigns each link
    // session its own controller ID (clients never choose one) and never reuses one while the host
    // runs; every session end calls Revoke; link calls are forwarded onto the host loop's thread,
    // never from inside a gateway event; in an owner-stepped run the loop gives the holder a bounded
    // window for command k before each step (paced: part of the step's period; unpaced: a stated wait)
    // and calls CheckHeartbeat while it waits; a lockstep run waits a bounded time for a claim after a
    // loss; Unity sessions never step (D2), so the server must know which session is Unity's; a
    // controller session needs a run with a heartbeat timeout, and the paced step within half of it,
    // or a healthy controller can be lost; a link session's ResetRun is accepted only from the
    // authority holder of a lockstep run (StepDriver.Controller), and it sends only a seed, so the
    // host resets its own scenario and a link can't change the spacecraft, physicalActuators or the
    // heartbeat timeout (CON-3; Reset is otherwise the owner's call); each session's
    // SessionStarted.max_message_bytes and unsent-message bound follow Argus.Contracts/README.md
    // "Controller gateway"; and controllers get a terminal observation's status, not its reason, which
    // can carry backend failure text (Failure is for logs, D7). Liveness is judged when a call is
    // processed, so the gateway's own step time counts as silence: pass each call's arrival time into
    // the gateway, or a step that overruns past the timeout drops a healthy controller (CORE-9 limit).
    // Add a GrpcServices="Server" item for this schema in the host csproj.
    internal sealed class GatewayServer
    {
    }
}
