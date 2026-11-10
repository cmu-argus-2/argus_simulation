namespace Argus.Simulation.Core
{
    // The "momentum_wheel" kind (D11, CON-2): a commanded motor torque about the wheel's spin axis,
    // idle at zero. Its state is the wheel's speed, given at t = 0 as initialState.
    public sealed class MomentumWheelKind : ActuatorKind<MomentumWheelProfile>
    {
        public const string KindName = "momentum_wheel";
        public const string MotorTorqueKey = "motor_torque_n_m";
        public const string SpeedKey = "speed_rad_per_s";

        public static readonly MomentumWheelKind Instance = new MomentumWheelKind();

        private const string SpinAxis = "spin_axis_body";
        private const string SpinInertia = "spin_inertia_kg_m2";
        private const string MaxTorque = "max_torque_n_m";
        private const string MaxSpeed = "max_speed_rad_per_s";

        private MomentumWheelKind()
            : base(
                KindName,
                new[] { new DeviceKey(MotorTorqueKey, DeviceValueType.Number) },
                new DeviceValues.Builder().Add(MotorTorqueKey, 0.0).Build(),
                new[]
                {
                    new DeviceKey(SpinAxis, DeviceValueType.Vector3),
                    new DeviceKey(SpinInertia, DeviceValueType.Number),
                    new DeviceKey(MaxTorque, DeviceValueType.Number),
                    new DeviceKey(MaxSpeed, DeviceValueType.Number),
                    new DeviceKey(LimitPolicyKey, DeviceValueType.Text)
                },
                new[] { new DeviceKey(SpeedKey, DeviceValueType.Number) })
        {
        }

        // A wheel configuration starting at initialSpeedRadiansPerSecond. The base Configure sets the
        // kind's source (DeviceSource, CORE-22); this adds the initial state.
        public DeviceConfiguration Configure(SensorDefinition definition, MomentumWheelProfile profile, double initialSpeedRadiansPerSecond)
        {
            DeviceConfiguration configuration = Configure(definition, profile);
            return new DeviceConfiguration(
                configuration.Definition,
                configuration.Kind,
                configuration.Parameters,
                new DeviceValues.Builder().Add(SpeedKey, initialSpeedRadiansPerSecond).Build());
        }

        protected override MomentumWheelProfile Parse(DeviceValues parameters) => new MomentumWheelProfile(
            parameters.Vector3(SpinAxis),
            parameters.Number(SpinInertia),
            parameters.Number(MaxTorque),
            parameters.Number(MaxSpeed),
            LimitPolicy(parameters));

        protected override DeviceValues ToParameters(MomentumWheelProfile profile) => new DeviceValues.Builder()
            .Add(SpinAxis, profile.SpinAxisBody)
            .Add(SpinInertia, profile.SpinInertiaKilogramSquareMeters)
            .Add(MaxTorque, profile.MaxTorqueNewtonMeters)
            .Add(MaxSpeed, profile.MaxSpeedRadiansPerSecond)
            .Add(LimitPolicyKey, ToValue(profile.LimitPolicy))
            .Build();

        protected override bool IsValid(MomentumWheelProfile profile) => profile.IsValid;

        protected override string FindProfileProblem(MomentumWheelProfile profile) => FirstProblem(
            (ActuatorGeometry.IsUnitAxis(profile.SpinAxisBody), SpinAxis, UnitAxis),
            (ActuatorGeometry.IsPositive(profile.SpinInertiaKilogramSquareMeters), SpinInertia, Positive),
            (ActuatorGeometry.IsPositive(profile.MaxTorqueNewtonMeters), MaxTorque, Positive),
            (ActuatorGeometry.IsPositive(profile.MaxSpeedRadiansPerSecond), MaxSpeed, Positive));
    }
}
