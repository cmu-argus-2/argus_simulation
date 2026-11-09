namespace Argus.Simulation.Core
{
    // The "thruster" kind (D11, CON-2): a commanded thrust held over the step, from 0 to
    // max_thrust_n, idle at zero. BSK-8 (#21) applies it as an on-time of thrust_n / max_thrust_n
    // of the step.
    public sealed class ThrusterKind : ActuatorKind<ThrusterProfile>
    {
        public const string KindName = "thruster";
        public const string ThrustKey = "thrust_n";

        public static readonly ThrusterKind Instance = new ThrusterKind();

        private const string ThrustDirection = "thrust_direction_body";
        private const string MaxThrust = "max_thrust_n";

        private ThrusterKind()
            : base(
                KindName,
                new[] { new DeviceKey(ThrustKey, DeviceValueType.Number) },
                new DeviceValues.Builder().Add(ThrustKey, 0.0).Build(),
                new[]
                {
                    new DeviceKey(ThrustDirection, DeviceValueType.Vector3),
                    new DeviceKey(MaxThrust, DeviceValueType.Number),
                    new DeviceKey(LimitPolicyKey, DeviceValueType.Text)
                })
        {
        }

        protected override ThrusterProfile Parse(DeviceValues parameters) => new ThrusterProfile(
            parameters.Vector3(ThrustDirection),
            parameters.Number(MaxThrust),
            LimitPolicy(parameters));

        protected override DeviceValues ToParameters(ThrusterProfile profile) => new DeviceValues.Builder()
            .Add(ThrustDirection, profile.ThrustDirectionBody)
            .Add(MaxThrust, profile.MaxThrustNewtons)
            .Add(LimitPolicyKey, ToValue(profile.LimitPolicy))
            .Build();

        protected override bool IsValid(ThrusterProfile profile) => profile.IsValid;

        protected override string FindProfileProblem(ThrusterProfile profile) => FirstProblem(
            (ActuatorGeometry.IsUnitAxis(profile.ThrustDirectionBody), ThrustDirection, UnitAxis),
            (ActuatorGeometry.IsPositive(profile.MaxThrustNewtons), MaxThrust, Positive));
    }
}
