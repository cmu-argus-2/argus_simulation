using System;

namespace Argus.Simulation.Core
{
    public enum AuthorityChange
    {
        // The controller now holds actuator authority: a new tenure, which starts from idle.
        Claimed = 0,

        // The claim was refused because another controller holds authority.
        Refused = 1,

        // The controller holding authority gave it up; the run is uncontrolled again.
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

            // A refusal or a loss says why; a claim or a release does not. A change nobody listed here
            // is refused.
            bool needsReason;
            switch (change)
            {
                case AuthorityChange.Claimed:
                case AuthorityChange.Released:
                    needsReason = false;
                    break;
                case AuthorityChange.Refused:
                case AuthorityChange.Lost:
                    needsReason = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(change), change, "Not a known authority change.");
            }

            if (needsReason == string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("Refused and Lost records need a reason, and only they have one.", nameof(reason));
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

        // Why a claim was refused or authority lost; null when Claimed or Released.
        public string Reason { get; }
    }
}
