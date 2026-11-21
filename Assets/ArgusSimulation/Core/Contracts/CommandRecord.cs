using System;

namespace Argus.Simulation.Core
{
    public enum CommandOutcome
    {
        // The gateway passed the command to the engine unchanged.
        Applied = 0,

        // The gateway passed a limited command to the engine (actuator limits, CORE-10).
        Clamped = 1,

        // The gateway passed nothing of this command on. When it was the call that steps (the owner's
        // Step(command), or the holder's in a lockstep run), the step did not run; a refused
        // submission in an owner-stepped run leaves the owner's next step as it would have been
        // without it (CORE-9).
        Rejected = 2,

        // No command arrived for the step from the controller holding authority, so the gateway passed
        // its tenure's last applied command again, restamped to the step, or every actuator's idle
        // values before the tenure's first command (CORE-9).
        Held = 3,

        // The controller holding authority was lost (a missed heartbeat or a revoke), so the gateway
        // passed every actuator's idle values until a controller claims authority again (CORE-9).
        Failsafe = 4
    }

    // One entry of the gateway's command log (D6, D7): a command the gateway received (a step or a
    // submission), or the command it made up for a held or failsafe step; what it passed to the
    // engine; and why it changed, held, failed safe or refused it. Sequence and
    // SimulationTimeSeconds are the gateway's own next step, k and t_k, so a command that claimed the
    // wrong step still joins to state k; Requested keeps what the controller sent. Immutable, so it is
    // safe to hand between threads.
    public sealed class CommandRecord
    {
        public CommandRecord(
            string runId,
            long sequence,
            double simulationTimeSeconds,
            CommandOutcome outcome,
            DeviceCommandSet requested,
            DeviceCommandSet? applied,
            string reason = null,
            string controllerId = null)
        {
            if (string.IsNullOrWhiteSpace(runId))
            {
                throw new ArgumentException("A run ID is required.", nameof(runId));
            }

            if (sequence < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence), "The sequence must be >= 0.");
            }

            if (double.IsNaN(simulationTimeSeconds) || double.IsInfinity(simulationTimeSeconds) || simulationTimeSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(simulationTimeSeconds), "The time must be finite and >= 0.");
            }

            // What each outcome carries, in one exhaustive switch: an outcome nobody listed here is
            // refused, so a new one cannot fall into a known one's rules. Applied has no reason; every
            // other outcome says why. Only Rejected passes nothing on. Nothing was sent for a held or
            // failsafe step, so its requested set is empty; a held step repeats the holder's command,
            // and a failsafe step belongs to no controller (CORE-9).
            bool passesCommand;
            bool hasReason;
            bool wasSent;
            bool? namesController;
            switch (outcome)
            {
                case CommandOutcome.Applied:
                    (passesCommand, hasReason, wasSent, namesController) = (true, false, true, null);
                    break;
                case CommandOutcome.Clamped:
                    (passesCommand, hasReason, wasSent, namesController) = (true, true, true, null);
                    break;
                case CommandOutcome.Held:
                    (passesCommand, hasReason, wasSent, namesController) = (true, true, false, true);
                    break;
                case CommandOutcome.Failsafe:
                    (passesCommand, hasReason, wasSent, namesController) = (true, true, false, false);
                    break;
                case CommandOutcome.Rejected:
                    (passesCommand, hasReason, wasSent, namesController) = (false, true, true, null);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a known command outcome.");
            }

            if (applied.HasValue != passesCommand || (applied.HasValue && !applied.Value.IsValid))
            {
                throw new ArgumentException("Every outcome but Rejected needs a valid applied command; Rejected has none.", nameof(applied));
            }

            if (hasReason == string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("Every outcome but Applied needs a reason, and Applied has none.", nameof(reason));
            }

            if (controllerId != null && string.IsNullOrWhiteSpace(controllerId))
            {
                throw new ArgumentException("A controller ID, when given, is not blank.", nameof(controllerId));
            }

            if (!wasSent && requested.Commands.Count != 0)
            {
                throw new ArgumentException("Held and Failsafe records have an empty requested set.", nameof(requested));
            }

            if (namesController.HasValue && namesController.Value != (controllerId != null))
            {
                throw new ArgumentException("A Held record names the controller holding authority; a Failsafe record names none.", nameof(controllerId));
            }

            RunId = runId;
            Sequence = sequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            Outcome = outcome;
            Requested = requested;
            Applied = applied;
            Reason = reason;
            ControllerId = controllerId;
        }

        // The controller that sent the command, or for Held the controller holding authority; null for
        // the gateway owner's own commands, for Failsafe, and for a command whose controller ID was
        // unusable, whose reason then says so (CORE-9).
        public string ControllerId { get; }

        public string RunId { get; }

        // The step the command arrived for: k, at t_k.
        public long Sequence { get; }
        public double SimulationTimeSeconds { get; }

        public CommandOutcome Outcome { get; }

        // As the controller sent it, even when it is invalid.
        public DeviceCommandSet Requested { get; }

        // What the engine ran over [t_k, t_k+1); none when Rejected.
        public DeviceCommandSet? Applied { get; }

        // Why the command was clamped, held, failed safe or rejected; null when Applied.
        public string Reason { get; }
    }
}
