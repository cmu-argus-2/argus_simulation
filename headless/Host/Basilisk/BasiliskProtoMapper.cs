using System;
using System.Collections.Generic;
using Argus.Simulation.Core;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using PbBasilisk = Argus.Contracts.Basilisk.V1;
using PbSim = Argus.Contracts.Sim.V1;

namespace Argus.Simulation.Host
{
    // Converts between generated Protobuf payloads and Core types; BasiliskEngine owns the client and
    // the request and response envelopes. Outgoing messages mirror Core contracts; incoming ones
    // become the internal Core/Basilisk mirrors, and any unset field throws.
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

            // Only backend-modelled devices go to Basilisk; Argus computes the rest (D10, CORE-18).
            foreach (DeviceConfiguration device in configuration.Devices)
            {
                if (device.IsBackendModelled)
                {
                    message.Devices.Add(ToProto(device));
                }
            }

            return message;
        }

        public static PbSim.DeviceCommandSet ToProto(DeviceCommandSet commands)
        {
            PbSim.DeviceCommandSet message = new PbSim.DeviceCommandSet();
            foreach (DeviceCommand command in commands.Commands)
            {
                PbSim.DeviceCommand device = new PbSim.DeviceCommand { DeviceId = command.DeviceId };
                AddAll(device.Values, command.Values);
                message.Commands.Add(device);
            }

            return message;
        }

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

            List<BasiliskSensorSample> samples = new List<BasiliskSensorSample>(response.DeviceSamples.Count);
            for (int i = 0; i < response.DeviceSamples.Count; i++)
            {
                PbBasilisk.BasiliskDeviceSample sample = response.DeviceSamples[i];
                samples.Add(new BasiliskSensorSample(
                    sample.DeviceId,
                    checked((long)sample.SampleTimeNs),
                    FromProto(sample.Output, $"device_samples[{i}].output")));
            }

            return new BasiliskStepState(
                checked((long)response.SimTimeNs),
                spacecraftState,
                FromProto(response.Earth, "earth"),
                FromProto(response.Sun, "sun"),
                response.SpacecraftShadowFactor,
                samples);
        }

        private static PbSim.DeviceConfiguration ToProto(DeviceConfiguration device)
        {
            SensorDefinition definition = device.Definition;
            PbSim.DeviceConfiguration message = new PbSim.DeviceConfiguration
            {
                DeviceId = definition.SensorId,
                Kind = device.Kind,
                SamplePeriodNs = checked((ulong)BasiliskTime.ToNanoseconds(definition.SamplePeriodSeconds)),
                ControllerVisible = definition.IsControllerVisible,
                Mount = new PbSim.DeviceMount
                {
                    PositionBodyM = ToProto(definition.Mount.PositionBodyMeters),
                    DeviceToBody = ToProto(definition.Mount.SensorToBody)
                }
            };
            AddAll(message.Parameters, device.Parameters);
            AddAll(message.InitialState, device.InitialState);
            return message;
        }

        private static void AddAll(MapField<string, PbSim.DeviceValue> map, DeviceValues values)
        {
            foreach (KeyValuePair<string, DeviceValue> entry in values)
            {
                map.Add(entry.Key, ToProto(entry.Value));
            }
        }

        private static PbSim.DeviceValue ToProto(DeviceValue value) => value.Type switch
        {
            DeviceValueType.Number => new PbSim.DeviceValue { Number = value.Number },
            DeviceValueType.Vector3 => new PbSim.DeviceValue { Vector3 = ToProto(value.Vector3) },
            DeviceValueType.Quaternion => new PbSim.DeviceValue { Quaternion = ToProto(value.Quaternion) },
            DeviceValueType.Matrix3 => new PbSim.DeviceValue { Matrix3 = ToProto(value.Matrix3) },
            DeviceValueType.Flag => new PbSim.DeviceValue { Flag = value.Flag },
            DeviceValueType.Text => new PbSim.DeviceValue { Text = value.Text },
            _ => throw new ArgumentException($"Device value type {value.Type} has no wire form.", nameof(value))
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

        // An empty map means the sensor ran without data; the mapper reports it Unavailable. The
        // kind checks the keys and types when it reads the map (BasiliskStateMapper).
        private static DeviceValues FromProto(MapField<string, PbSim.DeviceValue> map, string path)
        {
            DeviceValues.Builder values = new DeviceValues.Builder();
            foreach (KeyValuePair<string, PbSim.DeviceValue> entry in map)
            {
                values.Add(entry.Key, FromProto(entry.Value, $"{path}[\"{entry.Key}\"]"));
            }

            return values.Build();
        }

        private static DeviceValue FromProto(PbSim.DeviceValue value, string path)
        {
            switch (value?.ValueCase ?? PbSim.DeviceValue.ValueOneofCase.None)
            {
                case PbSim.DeviceValue.ValueOneofCase.Number:
                    return DeviceValue.FromNumber(value.Number);
                case PbSim.DeviceValue.ValueOneofCase.Vector3:
                    return DeviceValue.FromVector3(FromProto(value.Vector3, path + ".vector3"));
                case PbSim.DeviceValue.ValueOneofCase.Quaternion:
                    PbSim.Quaternion quaternion = value.Quaternion;
                    return DeviceValue.FromQuaternion(new Quaterniond(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W));
                case PbSim.DeviceValue.ValueOneofCase.Matrix3:
                    return DeviceValue.FromMatrix3(FromProto(value.Matrix3, path + ".matrix3"));
                case PbSim.DeviceValue.ValueOneofCase.Flag:
                    return DeviceValue.FromFlag(value.Flag);
                case PbSim.DeviceValue.ValueOneofCase.Text:
                    return DeviceValue.FromText(value.Text);
                default:
                    throw Unset(path);
            }
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
