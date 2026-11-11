namespace Argus.Simulation.Core
{
    // The "magnetometer" kind: Basilisk's magnetometer (TAM) on the WMM field (D10, D11). Keys are
    // MagnetometerProfile's fields in SI units.
    public sealed class MagnetometerKind : BackendSensorKind<MagnetometerProfile, MagnetometerMeasurement>
    {
        public const string KindName = "magnetometer";

        public static readonly MagnetometerKind Instance = new MagnetometerKind();

        private const string NoiseStd = "noise_std_tesla";
        private const string ConstantBias = "constant_bias_sensor_tesla";
        private const string ScaleFactor = "scale_factor";
        private const string Range = "range_tesla";
        private const string MagneticField = "magnetic_field_sensor_tesla";

        private MagnetometerKind()
            : base(
                KindName,
                new[] { new DeviceKey(MagneticField, DeviceValueType.Vector3) },
                new DeviceKey(NoiseStd, DeviceValueType.Number),
                new DeviceKey(ConstantBias, DeviceValueType.Vector3),
                new DeviceKey(ScaleFactor, DeviceValueType.Number),
                new DeviceKey(Range, DeviceValueType.Number))
        {
        }

        public override ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer) =>
            new MagnetometerSensor(configuration);

        protected override MagnetometerProfile Parse(DeviceValues parameters) => new MagnetometerProfile(
            parameters.Number(NoiseStd),
            parameters.Vector3(ConstantBias),
            parameters.Number(ScaleFactor),
            parameters.Number(Range));

        protected override DeviceValues ToParameters(MagnetometerProfile profile) => new DeviceValues.Builder()
            .Add(NoiseStd, profile.NoiseStdTesla)
            .Add(ConstantBias, profile.ConstantBiasSensorTesla)
            .Add(ScaleFactor, profile.ScaleFactor)
            .Add(Range, profile.RangeTesla)
            .Build();

        protected override bool IsValid(MagnetometerProfile profile) => profile.IsValid;

        protected override MagnetometerMeasurement Read(DeviceValues output) =>
            new MagnetometerMeasurement(output.Vector3(MagneticField));

        protected override DeviceValues Write(MagnetometerMeasurement measurement) =>
            new DeviceValues.Builder().Add(MagneticField, measurement.MagneticFieldSensorTesla).Build();
    }
}
