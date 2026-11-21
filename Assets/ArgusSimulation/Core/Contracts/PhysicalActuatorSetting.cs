namespace Argus.Simulation.Core
{
    // The physical-actuator inhibit (CORE-9, HIL-1 #66). Inhibited unless a run lifts it, and lifting
    // it names the test procedure that allows it, as the bench tool's "arm I_UNDERSTAND" does. The
    // gateway exposes it for HIL-1 to enforce; nothing in Core drives hardware.
    public readonly struct PhysicalActuatorSetting
    {
        public PhysicalActuatorSetting(bool enabled, string testProcedure)
        {
            Enabled = enabled;
            TestProcedure = testProcedure;
        }

        // The default: physical actuators inhibited, no procedure.
        public static PhysicalActuatorSetting Inhibited => default;

        public bool Enabled { get; }

        // Why the inhibit is lifted; required when Enabled, absent otherwise.
        public string TestProcedure { get; }

        // Lifted only with a procedure; a procedure without lifting would read as lifted, so it is refused.
        public bool IsValid => Enabled ? !string.IsNullOrWhiteSpace(TestProcedure) : TestProcedure == null;
    }
}
