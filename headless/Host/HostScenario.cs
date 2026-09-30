using System;
using Argus.Simulation.Core;

namespace Argus.Simulation.Host
{
    // PLACEHOLDER run configuration until the hardware team confirms parts and G5 defines a run
    // configuration file. Every value here is user input, not physics.
    public static class HostScenario
    {
        private const double SamplePeriodSeconds = 0.1;
        private const string Source = "basilisk";

        public static SimulationConfiguration CreateP0Default(string runId)
        {
            // Same elements as the Unity analytic defaults but not the same orbit: Basilisk measures
            // RAAN from J2000 x, while AnalyticEarthFixed measures it from Greenwich at the epoch
            // (Greenwich is about 114.4 degrees east of J2000 x at this epoch).
            ClassicalOrbitElements orbit = new ClassicalOrbitElements(6_878_137.0, 0.0, 51.6, 0.0, 0.0, 0.0);

            // 1U CubeSat.
            SpacecraftConfiguration spacecraft = new SpacecraftConfiguration(
                1.33,
                new Matrix3d(
                    new Vector3d(2.536e-3, 0.0, 0.0),
                    new Vector3d(0.0, 2.536e-3, 0.0),
                    new Vector3d(0.0, 0.0, 2.217e-3)),
                new Quaterniond(0.0, 0.0, 0.0, 1.0),
                new Vector3d(0.0, 0.0, 0.0));

            SensorConfiguration[] sensors =
            {
                Imu(),
                Magnetometer(),
                LightSensor("light.px", "light_px", new Vector3d(0.05, 0.0, 0.0), Y, Z, X),
                LightSensor("light.mx", "light_mx", new Vector3d(-0.05, 0.0, 0.0), -Y, Z, -X),
                LightSensor("light.py", "light_py", new Vector3d(0.0, 0.05, 0.0), Z, X, Y),
                LightSensor("light.my", "light_my", new Vector3d(0.0, -0.05, 0.0), -Z, X, -Y),
                LightSensor("light.pz", "light_pz", new Vector3d(0.0, 0.0, 0.05675), X, Y, Z),
                LightSensor("light.mz", "light_mz", new Vector3d(0.0, 0.0, -0.05675), X, -Y, -Z)
            };

            return new SimulationConfiguration(
                runId,
                new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
                0.1,
                initialOrbit: orbit,
                randomSeed: 1,
                kernelSetId: "argus-2026-09-25",
                spacecraft: spacecraft,
                sensors: sensors);
        }

        private static Vector3d X => new Vector3d(1.0, 0.0, 0.0);
        private static Vector3d Y => new Vector3d(0.0, 1.0, 0.0);
        private static Vector3d Z => new Vector3d(0.0, 0.0, 1.0);
        private static Vector3d Zero => new Vector3d(0.0, 0.0, 0.0);
        private static Vector3d UnitScale => new Vector3d(1.0, 1.0, 1.0);

        private static SensorConfiguration Imu()
        {
            GyroscopeProfile gyroscope = new GyroscopeProfile(1.2e-4, 0.0, 0.0, Zero, UnitScale, 4.363, 1.33e-4);
            AccelerometerProfile accelerometer = new AccelerometerProfile(1.8e-3, 0.0, 0.0, Zero, UnitScale, 19.61, 6.0e-4);
            return SensorConfiguration.ForImu(
                Definition("imu.main", "placeholder-mems-imu", "imu_main", SensorMount.Identity),
                new ImuProfile(gyroscope, accelerometer));
        }

        // Noise 0: bsk 2.11.1 cannot seed magnetometer noise, and the service rejects it (Argus.Basilisk/README.md).
        private static SensorConfiguration Magnetometer() => SensorConfiguration.ForMagnetometer(
            Definition("mag.main", "placeholder-magnetometer", "mag_main", SensorMount.Identity),
            new MagnetometerProfile(0.0, Zero, 1.0, 1.3e-3));

        // The sensor axes are given in body axes; the boresight is sensor +z.
        private static SensorConfiguration LightSensor(
            string sensorId,
            string frameId,
            Vector3d positionBodyMeters,
            Vector3d sensorX,
            Vector3d sensorY,
            Vector3d sensorZ)
        {
            SensorMount mount = new SensorMount(positionBodyMeters, Quaterniond.FromBasis(sensorX, sensorY, sensorZ));
            return SensorConfiguration.ForLightSensor(
                Definition(sensorId, "placeholder-face-light-sensor", frameId, mount),
                new LightSensorProfile(Math.PI / 2.0, 1.0, 1.0, 0.0, 2000.0));
        }

        private static SensorDefinition Definition(string sensorId, string modelName, string frameId, SensorMount mount) =>
            new SensorDefinition(sensorId, modelName, frameId, SamplePeriodSeconds, Source, mount);
    }
}
