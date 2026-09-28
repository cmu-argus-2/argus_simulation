namespace Argus.Simulation.Core
{
    // Optional development-control capability implemented by simulation backends
    // that support direct truth-attitude overrides.
    public interface IAttitudeOverrideTarget
    {
        Quaterniond AttitudeOverrideBody { get; }
        bool TryApplyAttitudeOverride(AttitudeOverrideCommand command);
    }
}
