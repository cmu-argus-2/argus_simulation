using System;
using System.Collections.Generic;
using Argus.Simulation.Core;
using Google.Protobuf.WellKnownTypes;
using PbBasilisk = Argus.Contracts.Basilisk.V1;
using PbSim = Argus.Contracts.Sim.V1;

namespace Argus.Simulation.Host
{
    // The only code that touches the generated Protobuf types. Outgoing messages mirror Core
    // contracts; incoming ones become the internal Core/Basilisk mirrors, and any unset field throws.
    internal static class BasiliskProtoMapper
    {
        public static PbSim.RunConfiguration ToProto(SimulationConfiguration configuration, long fixedStepNanoseconds)
        {
            ClassicalOrbitElements orbit = configuration.InitialOrbit.Value;
            SpacecraftConfiguration spacecraft = configuration.Spacecraft.Value;
            PbSim.RunConfiguration message = new PbSim.RunConfiguration
            {
                RunId = configuration.RunId,
                EpochUtc = Timestamp.FromDateTimeOffset(configuration.EpochUtc),
                FixedStepNs = checked((ulong)fixedStepNanoseconds),
                InitialOrbit = new PbSim.ClassicalOrbitElements
                {
                    SemiMajorAxisM = orbit.SemiMajorAxisMeters,
                    Eccentricity = orbit.Eccentricity,
                    InclinationDeg = orbit.InclinationDegrees,
                    RaanDeg = orbit.RaanDegrees,
                    ArgumentOfPeriapsisDeg = orbit.ArgumentOfPeriapsisDegrees,
                    TrueAnomalyDeg = orbit.TrueAnomalyDegrees
                },
                RandomSeed = checked((uint)configuration.RandomSeed),
                KernelSetId = configuration.KernelSetId,
                Spacecraft = new PbSim.SpacecraftConfiguration
                {
                    MassKg = spacecraft.MassKilograms,
                    InertiaBodyKgM2 = ToProto(spacecraft.InertiaBodyKilogramSquareMeters),
                    InitialBodyToJ2000 = ToProto(spacecraft.InitialBodyToJ2000),
                    InitialAngularVelocityBodyRadPerS = ToProto(spacecraft.InitialAngularVelocityBodyRadiansPerSecond)
                }
            };

            foreach (SensorConfiguration sensor in configuration.Sensors)
            {
                message.Sensors.Add(ToProto(sensor));
            }

            return message;
        }

        public static PbSim.ActuatorCommand ToProto(ActuatorCommandSet commands) => new PbSim.ActuatorCommand
        {
            ReactionWheelTorqueBodyNM = ToProto(commands.ReactionWheelTorqueBodyNewtonMeters),
            MagnetorquerDipoleBodyAM2 = ToProto(commands.MagnetorquerDipoleBodyAmpereSquareMeters),
            ThrusterForceBodyN = ToProto(commands.ThrusterForceBodyNewtons)
        };

        public static BasiliskStepState ToStepState(PbBasilisk.StepResponse response)
        {
            if (!response.HasSpacecraftShadowFactor)
            {
                throw Unset("spacecraft_shadow_factor");
            }

            PbBasilisk.BasiliskSpacecraftState spacecraft = response.Spacecraft ?? throw Unset("spacecraft");
            PbBasilisk.Mrp sigma = spacecraft.SigmaBn ?? throw Unset("spacecraft.sigma_bn");
            BasiliskSpacecraftState spacecraftState = new BasiliskSpacecraftState(
                FromProto(spacecraft.PositionBnInertialM, "spacecraft.position_bn_inertial_m"),
                FromProto(spacecraft.VelocityBnInertialMPerS, "spacecraft.velocity_bn_inertial_m_per_s"),
                new Mrp(sigma.X, sigma.Y, sigma.Z),
                FromProto(spacecraft.OmegaBnBodyRadPerS, "spacecraft.omega_bn_body_rad_per_s"));

            List<BasiliskSensorSample> samples = new List<BasiliskSensorSample>(response.SensorSamples.Count);
            for (int i = 0; i < response.SensorSamples.Count; i++)
            {
                samples.Add(FromProto(response.SensorSamples[i], $"sensor_samples[{i}]"));
            }

            return new BasiliskStepState(
                checked((long)response.SimTimeNs),
                spacecraftState,
                FromProto(response.Earth, "earth"),
                FromProto(response.Sun, "sun"),
                response.SpacecraftShadowFactor,
                samples);
        }

        private static PbSim.SensorConfiguration ToProto(SensorConfiguration sensor)
        {
            SensorDefinition definition = sensor.Definition;
            PbSim.SensorConfiguration message = new PbSim.SensorConfiguration
            {
                Definition = new PbSim.SensorDefinition
                {
                    SensorId = definition.SensorId,
                    SamplePeriodNs = checked((ulong)BasiliskTime.ToNanoseconds(definition.SamplePeriodSeconds)),
                    Mount = new PbSim.SensorMount
                    {
                        PositionBodyM = ToProto(definition.Mount.PositionBodyMeters),
                        SensorToBody = ToProto(definition.Mount.SensorToBody)
                    }
                }
            };

            switch (sensor.Kind)
            {
                case SensorKind.Imu:
                    message.Imu = new PbSim.ImuProfile
                    {
                        Gyroscope = ToProto(sensor.ImuProfile.Gyroscope),
                        Accelerometer = ToProto(sensor.ImuProfile.Accelerometer)
                    };
                    break;
                case SensorKind.Magnetometer:
                    MagnetometerProfile magnetometer = sensor.MagnetometerProfile;
                    message.Magnetometer = new PbSim.MagnetometerProfile
                    {
                        NoiseStdTesla = magnetometer.NoiseStdTesla,
                        ConstantBiasSensorTesla = ToProto(magnetometer.ConstantBiasSensorTesla),
                        ScaleFactor = magnetometer.ScaleFactor,
                        RangeTesla = magnetometer.RangeTesla
                    };
                    break;
                case SensorKind.LightSensor:
                    LightSensorProfile light = sensor.LightSensorProfile;
                    message.LightSensor = new PbSim.LightSensorProfile
                    {
                        FieldOfViewHalfAngleRad = light.FieldOfViewHalfAngleRadians,
                        ScaleFactor = light.ScaleFactor,
                        NoiseStdWPerM2 = light.NoiseStdWattsPerSquareMeter,
                        BiasWPerM2 = light.BiasWattsPerSquareMeter,
                        SaturationWPerM2 = light.SaturationWattsPerSquareMeter
                    };
                    break;
                default:
                    throw new ArgumentException($"Unsupported sensor kind {sensor.Kind}.", nameof(sensor));
            }

            return message;
        }

        private static PbSim.GyroscopeProfile ToProto(GyroscopeProfile profile) => new PbSim.GyroscopeProfile
        {
            NoiseDensityRadPerSPerSqrtHz = profile.NoiseDensityRadiansPerSecondPerRootHertz,
            BiasRandomWalkRadPerSPerSqrtS = profile.BiasRandomWalkRadiansPerSecondPerRootSecond,
            BiasRandomWalkBoundRadPerS = profile.BiasRandomWalkBoundRadiansPerSecond,
            ConstantBiasSensorRadPerS = ToProto(profile.ConstantBiasSensorRadiansPerSecond),
            ScaleFactorPerAxis = ToProto(profile.ScaleFactorPerAxis),
            RangeRadPerS = profile.RangeRadiansPerSecond,
            ResolutionRadPerS = profile.ResolutionRadiansPerSecond
        };

        private static PbSim.AccelerometerProfile ToProto(AccelerometerProfile profile) => new PbSim.AccelerometerProfile
        {
            NoiseDensityMPerS2PerSqrtHz = profile.NoiseDensityMetersPerSecondSquaredPerRootHertz,
            BiasRandomWalkMPerS2PerSqrtS = profile.BiasRandomWalkMetersPerSecondSquaredPerRootSecond,
            BiasRandomWalkBoundMPerS2 = profile.BiasRandomWalkBoundMetersPerSecondSquared,
            ConstantBiasSensorMPerS2 = ToProto(profile.ConstantBiasSensorMetersPerSecondSquared),
            ScaleFactorPerAxis = ToProto(profile.ScaleFactorPerAxis),
            RangeMPerS2 = profile.RangeMetersPerSecondSquared,
            ResolutionMPerS2 = profile.ResolutionMetersPerSecondSquared
        };

        private static PbSim.Vector3 ToProto(Vector3d value) =>
            new PbSim.Vector3 { X = value.X, Y = value.Y, Z = value.Z };

        private static PbSim.Quaternion ToProto(Quaterniond value) =>
            new PbSim.Quaternion { X = value.X, Y = value.Y, Z = value.Z, W = value.W };

        private static PbSim.Matrix3 ToProto(Matrix3d value) => new PbSim.Matrix3
        {
            Row0 = ToProto(value.Row0),
            Row1 = ToProto(value.Row1),
            Row2 = ToProto(value.Row2)
        };

        private static BasiliskPlanetState FromProto(PbBasilisk.BasiliskPlanetState planet, string path)
        {
            if (planet == null)
            {
                throw Unset(path);
            }

            return new BasiliskPlanetState(
                planet.PlanetName,
                FromProto(planet.PositionInertialM, path + ".position_inertial_m"),
                FromProto(planet.VelocityInertialMPerS, path + ".velocity_inertial_m_per_s"),
                FromProto(planet.J2000ToPlanetFixed, path + ".j2000_to_planet_fixed"),
                FromProto(planet.J2000ToPlanetFixedRatePerS, path + ".j2000_to_planet_fixed_rate_per_s"),
                planet.OrientationComputed);
        }

        // An unset measurement means the sensor ran without data; the mapper reports it Unavailable.
        private static BasiliskSensorSample FromProto(PbBasilisk.BasiliskSensorSample sample, string path)
        {
            object measurement;
            switch (sample.MeasurementCase)
            {
                case PbBasilisk.BasiliskSensorSample.MeasurementOneofCase.Imu:
                    measurement = new ImuMeasurement(
                        FromProto(sample.Imu.AngularVelocitySensorRadPerS, path + ".imu.angular_velocity_sensor_rad_per_s"),
                        FromProto(sample.Imu.SpecificForceSensorMPerS2, path + ".imu.specific_force_sensor_m_per_s2"));
                    break;
                case PbBasilisk.BasiliskSensorSample.MeasurementOneofCase.Magnetometer:
                    measurement = new MagnetometerMeasurement(
                        FromProto(sample.Magnetometer.MagneticFieldSensorTesla, path + ".magnetometer.magnetic_field_sensor_tesla"));
                    break;
                case PbBasilisk.BasiliskSensorSample.MeasurementOneofCase.LightSensor:
                    measurement = new LightSensorMeasurement(sample.LightSensor.IrradianceWPerM2);
                    break;
                default:
                    measurement = null;
                    break;
            }

            return new BasiliskSensorSample(sample.SensorId, checked((long)sample.SampleTimeNs), measurement);
        }

        private static Vector3d FromProto(PbSim.Vector3 value, string path) =>
            value == null ? throw Unset(path) : new Vector3d(value.X, value.Y, value.Z);

        private static Matrix3d FromProto(PbSim.Matrix3 value, string path)
        {
            if (value == null)
            {
                throw Unset(path);
            }

            return new Matrix3d(
                FromProto(value.Row0, path + ".row0"),
                FromProto(value.Row1, path + ".row1"),
                FromProto(value.Row2, path + ".row2"));
        }

        private static InvalidOperationException Unset(string path) =>
            new InvalidOperationException($"StepResponse.{path} is unset.");
    }
}
