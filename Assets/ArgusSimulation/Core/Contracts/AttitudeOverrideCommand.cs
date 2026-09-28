using System;

namespace Argus.Simulation.Core
{
    public enum AttitudeOverrideOperation
    {
        ApplyBodyFrameDelta = 0,
        Clear = 1
    }

    // Development control for changing analytic truth attitude without bypassing
    // the simulation controller. This is not an actuator or flight command.
    public readonly struct AttitudeOverrideCommand
    {
        public AttitudeOverrideCommand(
            long sequence,
            double applyAtSimulationTimeSeconds,
            AttitudeOverrideOperation operation,
            Quaterniond bodyFrameDelta)
        {
            Sequence = sequence;
            ApplyAtSimulationTimeSeconds = applyAtSimulationTimeSeconds;
            Operation = operation;
            BodyFrameDelta = bodyFrameDelta;
        }

        public long Sequence { get; }
        public double ApplyAtSimulationTimeSeconds { get; }
        public AttitudeOverrideOperation Operation { get; }
        public Quaterniond BodyFrameDelta { get; }

        public bool IsValid =>
            Sequence >= 0 &&
            ApplyAtSimulationTimeSeconds >= 0.0 &&
            !double.IsNaN(ApplyAtSimulationTimeSeconds) &&
            !double.IsInfinity(ApplyAtSimulationTimeSeconds) &&
            Enum.IsDefined(typeof(AttitudeOverrideOperation), Operation) &&
            (Operation == AttitudeOverrideOperation.Clear || BodyFrameDelta.IsUnit);

        public static AttitudeOverrideCommand ApplyDelta(
            long sequence,
            double applyAtSimulationTimeSeconds,
            Quaterniond bodyFrameDelta) =>
            new AttitudeOverrideCommand(
                sequence,
                applyAtSimulationTimeSeconds,
                AttitudeOverrideOperation.ApplyBodyFrameDelta,
                bodyFrameDelta);

        public static AttitudeOverrideCommand Clear(
            long sequence,
            double applyAtSimulationTimeSeconds) =>
            new AttitudeOverrideCommand(
                sequence,
                applyAtSimulationTimeSeconds,
                AttitudeOverrideOperation.Clear,
                Quaterniond.Identity);
    }
}
