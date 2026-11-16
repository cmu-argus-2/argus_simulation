namespace Argus.Simulation.Core
{
    // The "magnetic_torquer" kind (D11, CON-2): a commanded signed dipole along the torquer's axis,
    // idle at zero.
    public sealed class MagneticTorquerKind : ActuatorKind<MagneticTorquerProfile>
    {
        public const string KindName = "magnetic_torquer";
        public const string DipoleKey = "dipole_a_m2";

        public static readonly MagneticTorquerKind Instance = new MagneticTorquerKind();

        private const string DipoleAxis = "dipole_axis_body";
        private const string MaxDipole = "max_dipole_a_m2";

        private MagneticTorquerKind()
            : base(
                KindName,
                new[] { new DeviceKey(DipoleKey, DeviceValueType.Number) },
                new DeviceValues.Builder().Add(DipoleKey, 0.0).Build(),
                new[]
                {
                    new DeviceKey(DipoleAxis, DeviceValueType.Vector3),
                    new DeviceKey(MaxDipole, DeviceValueType.Number),
                    new DeviceKey(LimitPolicyKey, DeviceValueType.Text)
                })
        {
        }

        protected override MagneticTorquerProfile Parse(DeviceValues parameters) => new MagneticTorquerProfile(
            parameters.Vector3(DipoleAxis),
            parameters.Number(MaxDipole),
            LimitPolicy(parameters));

        protected override DeviceValues ToParameters(MagneticTorquerProfile profile) => new DeviceValues.Builder()
            .Add(DipoleAxis, profile.DipoleAxisBody)
            .Add(MaxDipole, profile.MaxDipoleAmpereSquareMeters)
            .Add(LimitPolicyKey, ToValue(profile.LimitPolicy))
            .Build();

        protected override bool IsValid(MagneticTorquerProfile profile) => profile.IsValid;

        // |dipole| <= max_dipole_a_m2, either sign.
        protected override DeviceValues LimitToProfile(MagneticTorquerProfile profile, DeviceValues values, out string excess) =>
            LimitMagnitude(values, DipoleKey, profile.MaxDipoleAmpereSquareMeters, MaxDipole, out excess);

        protected override string FindProfileProblem(MagneticTorquerProfile profile) => FirstProblem(
            (ActuatorGeometry.IsUnitAxis(profile.DipoleAxisBody), DipoleAxis, UnitAxis),
            (ActuatorGeometry.IsPositive(profile.MaxDipoleAmpereSquareMeters), MaxDipole, Positive));
    }
}
