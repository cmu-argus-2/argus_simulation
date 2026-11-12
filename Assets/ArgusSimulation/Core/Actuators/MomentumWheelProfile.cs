namespace Argus.Simulation.Core
{
    // A wheel spun by its motor about a body-fixed axis (D11). Comments name the Basilisk
    // reactionWheelStateEffector field each would set; the service doesn't build wheels yet and
    // refuses a run that configures one (UNIMPLEMENTED; Argus.Basilisk/README.md §9 "Actuators").
    public readonly struct MomentumWheelProfile
    {
        public MomentumWheelProfile(
            Vector3d spinAxisBody,
            double spinInertiaKilogramSquareMeters,
            double maxTorqueNewtonMeters,
            double maxSpeedRadiansPerSecond,
            ActuatorLimitPolicy limitPolicy)
        {
            SpinAxisBody = spinAxisBody;
            SpinInertiaKilogramSquareMeters = spinInertiaKilogramSquareMeters;
            MaxTorqueNewtonMeters = maxTorqueNewtonMeters;
            MaxSpeedRadiansPerSecond = maxSpeedRadiansPerSecond;
            LimitPolicy = limitPolicy;
        }

        // gsHat_B, a unit vector.
        public Vector3d SpinAxisBody { get; }

        // Js.
        public double SpinInertiaKilogramSquareMeters { get; }

        // u_max.
        public double MaxTorqueNewtonMeters { get; }

        // Omega_max.
        public double MaxSpeedRadiansPerSecond { get; }

        public ActuatorLimitPolicy LimitPolicy { get; }

        public bool IsValid =>
            ActuatorGeometry.IsUnitAxis(SpinAxisBody) &&
            ActuatorGeometry.IsPositive(SpinInertiaKilogramSquareMeters) &&
            ActuatorGeometry.IsPositive(MaxTorqueNewtonMeters) &&
            ActuatorGeometry.IsPositive(MaxSpeedRadiansPerSecond) &&
            LimitPolicy != ActuatorLimitPolicy.Unknown;
    }
}
