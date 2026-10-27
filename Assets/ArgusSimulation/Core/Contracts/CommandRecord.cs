using System;

namespace Argus.Simulation.Core
{
    public enum CommandOutcome
    {
        // The gateway passed the command to the engine unchanged.
        Applied = 0,

        // The gateway passed a limited command to the engine (actuator limits, CORE-10).
        Clamped = 1,

        // The gateway passed nothing to the engine, so the step did not run.
        Rejected = 2
    }

    // One entry of the gateway's command log (D6, D7): a command SimulationGateway.Step received,
    // what the gateway passed to the engine, and why it changed or refused it. Sequence and
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
            ActuatorCommandSet requested,
            ActuatorCommandSet? applied,
            string reason = null)
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

            // Applied has no reason; Clamped and Rejected say why. Only Rejected passes nothing on.
            bool passesCommand = outcome == CommandOutcome.Applied || outcome == CommandOutcome.Clamped;
            if (!passesCommand && outcome != CommandOutcome.Rejected)
            {
                throw new ArgumentOutOfRangeException(nameof(outcome));
            }

            if (applied.HasValue != passesCommand || (applied.HasValue && !applied.Value.IsValid))
            {
                throw new ArgumentException("Applied and Clamped records need a valid applied command; Rejected ones have none.", nameof(applied));
            }

            if ((outcome == CommandOutcome.Applied) != string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("Clamped and Rejected records need a reason, and only they have one.", nameof(reason));
            }

            RunId = runId;
            Sequence = sequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            Outcome = outcome;
            Requested = requested;
            Applied = applied;
            Reason = reason;
        }

        public string RunId { get; }

        // The step the command arrived for: k, at t_k.
        public long Sequence { get; }
        public double SimulationTimeSeconds { get; }

        public CommandOutcome Outcome { get; }

        // As the controller sent it, even when it is invalid.
        public ActuatorCommandSet Requested { get; }

        // What the engine ran over [t_k, t_k+1); none when Rejected.
        public ActuatorCommandSet? Applied { get; }

        // Why the command was clamped or rejected; null when Applied.
        public string Reason { get; }
    }
}
