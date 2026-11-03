namespace Argus.Simulation.Core
{
    // The "imu" kind: a gyroscope and an accelerometer sharing a mount, period and frame, as
    // Basilisk's imuSensor models them (D11). Keys are the gyroscope.* and accelerometer.* fields
    // of ImuProfile, in SI units; Argus.Basilisk/README.md §9 maps each to its Basilisk attribute.
    public sealed class ImuKind : BackendSensorKind<ImuProfile, ImuMeasurement>
    {
        public const string KindName = "imu";

        public static readonly ImuKind Instance = new ImuKind();

        private const string GyroNoiseDensity = "gyroscope.noise_density_rad_per_s_per_sqrt_hz";
        private const string GyroRandomWalk = "gyroscope.bias_random_walk_rad_per_s_per_sqrt_s";
        private const string GyroRandomWalkBound = "gyroscope.bias_random_walk_bound_rad_per_s";
        private const string GyroConstantBias = "gyroscope.constant_bias_sensor_rad_per_s";
        private const string GyroScaleFactor = "gyroscope.scale_factor_per_axis";
        private const string GyroRange = "gyroscope.range_rad_per_s";
        private const string GyroResolution = "gyroscope.resolution_rad_per_s";
        private const string AccelNoiseDensity = "accelerometer.noise_density_m_per_s2_per_sqrt_hz";
        private const string AccelRandomWalk = "accelerometer.bias_random_walk_m_per_s2_per_sqrt_s";
        private const string AccelRandomWalkBound = "accelerometer.bias_random_walk_bound_m_per_s2";
        private const string AccelConstantBias = "accelerometer.constant_bias_sensor_m_per_s2";
        private const string AccelScaleFactor = "accelerometer.scale_factor_per_axis";
        private const string AccelRange = "accelerometer.range_m_per_s2";
        private const string AccelResolution = "accelerometer.resolution_m_per_s2";
        private const string AngularVelocity = "angular_velocity_sensor_rad_per_s";
        private const string SpecificForce = "specific_force_sensor_m_per_s2";

        private ImuKind()
            : base(
                KindName,
                new[]
                {
                    new DeviceKey(AngularVelocity, DeviceValueType.Vector3),
                    new DeviceKey(SpecificForce, DeviceValueType.Vector3)
                },
                new DeviceKey(GyroNoiseDensity, DeviceValueType.Number),
                new DeviceKey(GyroRandomWalk, DeviceValueType.Number),
                new DeviceKey(GyroRandomWalkBound, DeviceValueType.Number),
                new DeviceKey(GyroConstantBias, DeviceValueType.Vector3),
                new DeviceKey(GyroScaleFactor, DeviceValueType.Vector3),
                new DeviceKey(GyroRange, DeviceValueType.Number),
                new DeviceKey(GyroResolution, DeviceValueType.Number),
                new DeviceKey(AccelNoiseDensity, DeviceValueType.Number),
                new DeviceKey(AccelRandomWalk, DeviceValueType.Number),
                new DeviceKey(AccelRandomWalkBound, DeviceValueType.Number),
                new DeviceKey(AccelConstantBias, DeviceValueType.Vector3),
                new DeviceKey(AccelScaleFactor, DeviceValueType.Vector3),
                new DeviceKey(AccelRange, DeviceValueType.Number),
                new DeviceKey(AccelResolution, DeviceValueType.Number))
        {
        }

        public override ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer) =>
            new ImuSensor(configuration);

        protected override ImuProfile Parse(DeviceValues parameters) => new ImuProfile(
            new GyroscopeProfile(
                parameters.Number(GyroNoiseDensity),
                parameters.Number(GyroRandomWalk),
                parameters.Number(GyroRandomWalkBound),
                parameters.Vector3(GyroConstantBias),
                parameters.Vector3(GyroScaleFactor),
                parameters.Number(GyroRange),
                parameters.Number(GyroResolution)),
            new AccelerometerProfile(
                parameters.Number(AccelNoiseDensity),
                parameters.Number(AccelRandomWalk),
                parameters.Number(AccelRandomWalkBound),
                parameters.Vector3(AccelConstantBias),
                parameters.Vector3(AccelScaleFactor),
                parameters.Number(AccelRange),
                parameters.Number(AccelResolution)));

        protected override DeviceValues ToParameters(ImuProfile profile)
        {
            GyroscopeProfile gyroscope = profile.Gyroscope;
            AccelerometerProfile accelerometer = profile.Accelerometer;
            return new DeviceValues.Builder()
                .Add(GyroNoiseDensity, gyroscope.NoiseDensityRadiansPerSecondPerRootHertz)
                .Add(GyroRandomWalk, gyroscope.BiasRandomWalkRadiansPerSecondPerRootSecond)
                .Add(GyroRandomWalkBound, gyroscope.BiasRandomWalkBoundRadiansPerSecond)
                .Add(GyroConstantBias, gyroscope.ConstantBiasSensorRadiansPerSecond)
                .Add(GyroScaleFactor, gyroscope.ScaleFactorPerAxis)
                .Add(GyroRange, gyroscope.RangeRadiansPerSecond)
                .Add(GyroResolution, gyroscope.ResolutionRadiansPerSecond)
                .Add(AccelNoiseDensity, accelerometer.NoiseDensityMetersPerSecondSquaredPerRootHertz)
                .Add(AccelRandomWalk, accelerometer.BiasRandomWalkMetersPerSecondSquaredPerRootSecond)
                .Add(AccelRandomWalkBound, accelerometer.BiasRandomWalkBoundMetersPerSecondSquared)
                .Add(AccelConstantBias, accelerometer.ConstantBiasSensorMetersPerSecondSquared)
                .Add(AccelScaleFactor, accelerometer.ScaleFactorPerAxis)
                .Add(AccelRange, accelerometer.RangeMetersPerSecondSquared)
                .Add(AccelResolution, accelerometer.ResolutionMetersPerSecondSquared)
                .Build();
        }

        protected override bool IsValid(ImuProfile profile) => profile.IsValid;

        // Names the channel, as the service does with the key (validation.py).
        protected override string FindProfileProblem(ImuProfile profile)
        {
            if (!profile.Gyroscope.IsValid)
            {
                return "parameters[\"gyroscope.*\"]: is not a valid gyroscope profile (see GyroscopeProfile.IsValid)";
            }

            return profile.Accelerometer.IsValid
                ? null
                : "parameters[\"accelerometer.*\"]: is not a valid accelerometer profile (see AccelerometerProfile.IsValid)";
        }

        protected override ImuMeasurement Read(DeviceValues output) =>
            new ImuMeasurement(output.Vector3(AngularVelocity), output.Vector3(SpecificForce));
    }
}
