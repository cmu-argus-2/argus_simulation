namespace Argus.Simulation.Core
{
    // The "ideal_body_torque" kind (D11, CON-2): a commanded body-frame torque, idle at zero.
    public sealed class IdealBodyTorqueKind : ActuatorKind<IdealBodyTorqueProfile>
    {
        public const string KindName = "ideal_body_torque";
        public const string TorqueKey = "torque_body_n_m";

        public static readonly IdealBodyTorqueKind Instance = new IdealBodyTorqueKind();

        private const string MaxTorque = "max_torque_body_n_m";

        private IdealBodyTorqueKind()
            : base(
                KindName,
                new[] { new DeviceKey(TorqueKey, DeviceValueType.Vector3) },
                new DeviceValues.Builder().Add(TorqueKey, new Vector3d(0.0, 0.0, 0.0)).Build(),
                new[]
                {
                    new DeviceKey(MaxTorque, DeviceValueType.Vector3),
                    new DeviceKey(LimitPolicyKey, DeviceValueType.Text)
                })
        {
        }

        protected override IdealBodyTorqueProfile Parse(DeviceValues parameters) =>
            new IdealBodyTorqueProfile(parameters.Vector3(MaxTorque), LimitPolicy(parameters));

        protected override DeviceValues ToParameters(IdealBodyTorqueProfile profile) => new DeviceValues.Builder()
            .Add(MaxTorque, profile.MaxTorqueBodyNewtonMeters)
            .Add(LimitPolicyKey, ToValue(profile.LimitPolicy))
            .Build();

        protected override bool IsValid(IdealBodyTorqueProfile profile) => profile.IsValid;

        protected override string FindProfileProblem(IdealBodyTorqueProfile profile)
        {
            Vector3d max = profile.MaxTorqueBodyNewtonMeters;
            bool isPositive = ActuatorGeometry.IsPositive(max.X) &&
                ActuatorGeometry.IsPositive(max.Y) &&
                ActuatorGeometry.IsPositive(max.Z);
            return FirstProblem((isPositive, MaxTorque, PositiveComponents));
        }
    }
}
