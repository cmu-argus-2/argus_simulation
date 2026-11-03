namespace Argus.Simulation.Core
{
    // The "light_sensor" kind: Basilisk's coarseSunSensor with Earth albedo, reporting irradiance on
    // its aperture (D10, D11). Keys are LightSensorProfile's fields in SI units.
    public sealed class LightSensorKind : BackendSensorKind<LightSensorProfile, LightSensorMeasurement>
    {
        public const string KindName = "light_sensor";

        public static readonly LightSensorKind Instance = new LightSensorKind();

        private const string FieldOfView = "field_of_view_half_angle_rad";
        private const string ScaleFactor = "scale_factor";
        private const string NoiseStd = "noise_std_w_per_m2";
        private const string Bias = "bias_w_per_m2";
        private const string Saturation = "saturation_w_per_m2";
        private const string Irradiance = "irradiance_w_per_m2";

        private LightSensorKind()
            : base(
                KindName,
                new[] { new DeviceKey(Irradiance, DeviceValueType.Number) },
                new DeviceKey(FieldOfView, DeviceValueType.Number),
                new DeviceKey(ScaleFactor, DeviceValueType.Number),
                new DeviceKey(NoiseStd, DeviceValueType.Number),
                new DeviceKey(Bias, DeviceValueType.Number),
                new DeviceKey(Saturation, DeviceValueType.Number))
        {
        }

        public override ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer) =>
            new LightSensor(configuration);

        protected override LightSensorProfile Parse(DeviceValues parameters) => new LightSensorProfile(
            parameters.Number(FieldOfView),
            parameters.Number(ScaleFactor),
            parameters.Number(NoiseStd),
            parameters.Number(Bias),
            parameters.Number(Saturation));

        protected override DeviceValues ToParameters(LightSensorProfile profile) => new DeviceValues.Builder()
            .Add(FieldOfView, profile.FieldOfViewHalfAngleRadians)
            .Add(ScaleFactor, profile.ScaleFactor)
            .Add(NoiseStd, profile.NoiseStdWattsPerSquareMeter)
            .Add(Bias, profile.BiasWattsPerSquareMeter)
            .Add(Saturation, profile.SaturationWattsPerSquareMeter)
            .Build();

        protected override bool IsValid(LightSensorProfile profile) => profile.IsValid;

        protected override LightSensorMeasurement Read(DeviceValues output) =>
            new LightSensorMeasurement(output.Number(Irradiance));
    }
}
