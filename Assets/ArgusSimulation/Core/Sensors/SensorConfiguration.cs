using System;

namespace Argus.Simulation.Core
{
    // Append new values only; never renumber.
    public enum SensorKind
    {
        Unknown = 0,
        Imu = 1,
        Magnetometer = 2,
        LightSensor = 3,
        Camera = 4
    }

    // One sensor of the run configuration (G5): its definition and the profile its kind is modelled
    // with. Build it with the factory for its kind; only the matching profile is set.
    public readonly struct SensorConfiguration
    {
        private SensorConfiguration(
            SensorDefinition definition,
            SensorKind kind,
            ImuProfile imuProfile,
            MagnetometerProfile magnetometerProfile,
            LightSensorProfile lightSensorProfile,
            CameraProfile cameraProfile)
        {
            Definition = definition;
            Kind = kind;
            ImuProfile = imuProfile;
            MagnetometerProfile = magnetometerProfile;
            LightSensorProfile = lightSensorProfile;
            CameraProfile = cameraProfile;
        }

        public SensorDefinition Definition { get; }
        public SensorKind Kind { get; }
        public ImuProfile ImuProfile { get; }
        public MagnetometerProfile MagnetometerProfile { get; }
        public LightSensorProfile LightSensorProfile { get; }
        public CameraProfile CameraProfile { get; }

        public bool IsValid => Definition.IsValid && Kind switch
        {
            SensorKind.Imu => ImuProfile.IsValid,
            SensorKind.Magnetometer => MagnetometerProfile.IsValid,
            SensorKind.LightSensor => LightSensorProfile.IsValid,
            SensorKind.Camera => CameraProfile.IsValid,
            _ => false
        };

        // Where each kind is computed (D10, CORE-18). Backend-modelled sensors keep their physics and
        // cadence in the dynamics backend: only they go to ConfigureRun and MapMeasurements, and Argus
        // never computes them. Argus-computed kinds never reach the backend. Every kind must be listed:
        // an unlisted kind throws instead of silently never being sent or sampled.
        public bool IsBackendModelled => Kind switch
        {
            SensorKind.Imu or SensorKind.Magnetometer or SensorKind.LightSensor => true,
            SensorKind.Camera or SensorKind.Unknown => false,
            _ => throw new InvalidOperationException(
                $"Sensor kind {Kind} does not declare whether the backend models it (D10).")
        };

        public static SensorConfiguration ForImu(SensorDefinition definition, ImuProfile profile) =>
            new SensorConfiguration(definition, SensorKind.Imu, profile, default, default, default);

        public static SensorConfiguration ForMagnetometer(SensorDefinition definition, MagnetometerProfile profile) =>
            new SensorConfiguration(definition, SensorKind.Magnetometer, default, profile, default, default);

        public static SensorConfiguration ForLightSensor(SensorDefinition definition, LightSensorProfile profile) =>
            new SensorConfiguration(definition, SensorKind.LightSensor, default, default, profile, default);

        public static SensorConfiguration ForCamera(SensorDefinition definition, CameraProfile profile) =>
            new SensorConfiguration(definition, SensorKind.Camera, default, default, default, profile);
    }
}
