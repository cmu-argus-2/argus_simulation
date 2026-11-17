using System;

namespace Argus.Simulation.Core
{
    public enum AuthorityChange
    {
        // The controller now holds actuator authority: a new tenure, which starts from idle.
        Claimed = 0,

        // The claim was refused because another controller holds authority.
        Refused = 1,

        // The controller holding authority gave it up, or its run ended (stopped, failed, finished or
        // reset), which the reason then says; the run is uncontrolled again.
        Released = 2,

        // The controller holding authority lost it: a missed heartbeat or a revoke (CORE-9). The
        // run fails safe until a controller claims again.
        Lost = 3
    }

    // One entry of the gateway's authority log (D6, CORE-9): who claimed, released or lost actuator
    // authority, or was refused it. Sequence and SimulationTimeSeconds are the gateway's next step,
    // k and t_k, when it happened. Immutable, so it is safe to hand between threads.
    public sealed class AuthorityRecord
    {
        public AuthorityRecord(
            string runId,
            long sequence,
            double simulationTimeSeconds,
            string controllerId,
            AuthorityChange change,
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

            if (string.IsNullOrWhiteSpace(controllerId))
            {
                throw new ArgumentException("A controller ID is required.", nameof(controllerId));
            }

            // A refusal or a loss says why, and a claim does not; a release says why only when the run's
            // end made it, and a given reason is never blank. A change nobody listed here is refused.
            bool? hasReason;
            switch (change)
            {
                case AuthorityChange.Claimed:
                    hasReason = false;
                    break;
                case AuthorityChange.Released:
                    hasReason = null;
                    break;
                case AuthorityChange.Refused:
                case AuthorityChange.Lost:
                    hasReason = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(change), change, "Not a known authority change.");
            }

            if (reason != null && string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("A reason, when given, is not blank.", nameof(reason));
            }

            if (hasReason.HasValue && hasReason.Value != (reason != null))
            {
                throw new ArgumentException("Refused and Lost records need a reason, and a Claimed record has none.", nameof(reason));
            }

            RunId = runId;
            Sequence = sequence;
            SimulationTimeSeconds = simulationTimeSeconds;
            ControllerId = controllerId;
            Change = change;
            Reason = reason;
        }

        public string RunId { get; }

        // The gateway's next step when the change happened: k, at t_k.
        public long Sequence { get; }
        public double SimulationTimeSeconds { get; }

        public string ControllerId { get; }
        public AuthorityChange Change { get; }

        // Why a claim was refused, authority lost, or a tenure ended with its run; null when Claimed
        // or released by the holder.
        public string Reason { get; }
    }
}
