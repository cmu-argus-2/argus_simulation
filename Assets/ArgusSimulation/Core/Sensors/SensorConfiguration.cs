namespace Argus.Simulation.Core
{
    // Append new values only; never renumber.
    public enum SensorKind
    {
        Unknown = 0,
        Imu = 1,
        Magnetometer = 2,
        LightSensor = 3
    }

    // One sensor of the run configuration (G5): its definition and the profile the backend models it
    // with. Build it with the factory for its kind; only the matching profile is set.
    public readonly struct SensorConfiguration
    {
        private SensorConfiguration(
            SensorDefinition definition,
            SensorKind kind,
            ImuProfile imuProfile,
            MagnetometerProfile magnetometerProfile,
            LightSensorProfile lightSensorProfile)
        {
            Definition = definition;
            Kind = kind;
            ImuProfile = imuProfile;
            MagnetometerProfile = magnetometerProfile;
            LightSensorProfile = lightSensorProfile;
        }

        public SensorDefinition Definition { get; }
        public SensorKind Kind { get; }
        public ImuProfile ImuProfile { get; }
        public MagnetometerProfile MagnetometerProfile { get; }
        public LightSensorProfile LightSensorProfile { get; }

        public bool IsValid => Definition.IsValid && Kind switch
        {
            SensorKind.Imu => ImuProfile.IsValid,
            SensorKind.Magnetometer => MagnetometerProfile.IsValid,
            SensorKind.LightSensor => LightSensorProfile.IsValid,
            _ => false
        };

        public static SensorConfiguration ForImu(SensorDefinition definition, ImuProfile profile) =>
            new SensorConfiguration(definition, SensorKind.Imu, profile, default, default);

        public static SensorConfiguration ForMagnetometer(SensorDefinition definition, MagnetometerProfile profile) =>
            new SensorConfiguration(definition, SensorKind.Magnetometer, default, profile, default);

        public static SensorConfiguration ForLightSensor(SensorDefinition definition, LightSensorProfile profile) =>
            new SensorConfiguration(definition, SensorKind.LightSensor, default, default, profile);
    }
}
