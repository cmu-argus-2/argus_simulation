using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // Converts one Basilisk step into Argus contracts (G4). Every inconsistent input throws, since it
    // means the service or its SPICE setup is wrong; nothing is repaired or approximated (D3).
    internal static class BasiliskStateMapper
    {
        private const double OrthonormalTolerance = 1e-9;
        private const double NominalEarthRotationRadiansPerSecond = 7.2921150e-5;
        private const double EarthRotationToleranceRadiansPerSecond = 1e-8;
        private const double MinimumSunDistanceMeters = 1.40e11;
        private const double MaximumSunDistanceMeters = 1.60e11;

        public static EnvironmentState MapEnvironment(BasiliskStepState step)
        {
            if (!step.IsValid)
            {
                throw new ArgumentException("The Basilisk step state is not valid.", nameof(step));
            }

            if (!string.Equals(step.Earth.PlanetName, "earth", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(step.Sun.PlanetName, "sun", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Expected planets 'earth' and 'sun', got '{step.Earth.PlanetName}' and '{step.Sun.PlanetName}'.",
                    nameof(step));
            }

            if (!step.Earth.OrientationComputed)
            {
                throw new ArgumentException(
                    "Earth's orientation was not computed; set spiceInterface planetFrames to ITRF93.",
                    nameof(step));
            }

            Matrix3d rotation = step.Earth.J2000ToPlanetFixed;
            Matrix3d rotationRate = step.Earth.J2000ToPlanetFixedRatePerSecond;
            Vector3d r0 = rotation.Row0;
            Vector3d r1 = rotation.Row1;
            Vector3d r2 = rotation.Row2;
            if (!IsOrthonormal(r0, r1, r2) || Vector3d.Dot(Vector3d.Cross(r0, r1), r2) <= 0.0)
            {
                throw new ArgumentException("J20002Pfix is not a proper rotation.", nameof(step));
            }

            // FromBasis takes the images of the J2000 axes, which are the columns of R.
            Quaterniond j2000ToItrf93 = Quaterniond.FromBasis(
                new Vector3d(r0.X, r1.X, r2.X),
                new Vector3d(r0.Y, r1.Y, r2.Y),
                new Vector3d(r0.Z, r1.Z, r2.Z));

            // dR/dt = -[w x] R, so dR/dt R^T = -[w x]; take its antisymmetric part.
            Vector3d d0 = rotationRate.Row0;
            Vector3d d1 = rotationRate.Row1;
            Vector3d d2 = rotationRate.Row2;
            Vector3d earthRate = new Vector3d(
                (Vector3d.Dot(d1, r2) - Vector3d.Dot(d2, r1)) / 2.0,
                (Vector3d.Dot(d2, r0) - Vector3d.Dot(d0, r2)) / 2.0,
                (Vector3d.Dot(d0, r1) - Vector3d.Dot(d1, r0)) / 2.0);
            if (Math.Abs(earthRate.Magnitude - NominalEarthRotationRadiansPerSecond) > EarthRotationToleranceRadiansPerSecond)
            {
                throw new ArgumentException(
                    $"Earth rotation rate {earthRate.Magnitude:R} rad/s is implausible; was J20002Pfix_dot copied?",
                    nameof(step));
            }

            // ITRF93 +z is the spin axis to about 1e-6 rad; a transposed R and dR/dt pass every other
            // check but spin Earth about -z.
            if (Math.Abs(earthRate.Z - NominalEarthRotationRadiansPerSecond) > EarthRotationToleranceRadiansPerSecond)
            {
                throw new ArgumentException(
                    $"Earth rotation axis {earthRate} is not ITRF93 +z; are J20002Pfix and its rate transposed?",
                    nameof(step));
            }

            Vector3d sunFromEarthJ2000 = step.Sun.PositionInertialMeters - step.Earth.PositionInertialMeters;
            double sunDistance = sunFromEarthJ2000.Magnitude;
            if (sunDistance < MinimumSunDistanceMeters || sunDistance > MaximumSunDistanceMeters)
            {
                throw new ArgumentException(
                    $"Sun distance {sunDistance:R} m is implausible; check the units.",
                    nameof(step));
            }

            EnvironmentState environment = new EnvironmentState(
                j2000ToItrf93.Rotate(sunFromEarthJ2000),
                j2000ToItrf93,
                earthRate,
                step.SpacecraftShadowFactor);
            if (!environment.IsValid)
            {
                throw new ArgumentException("The mapped environment is not valid.", nameof(step));
            }

            return environment;
        }

        // environment must be MapEnvironment(step).
        public static SpacecraftState MapSpacecraft(
            long sequence,
            DateTimeOffset epochUtc,
            BasiliskStepState step,
            EnvironmentState environment)
        {
            Quaterniond j2000ToItrf93 = environment.J2000ToItrf93;
            Vector3d earthRate = environment.EarthAngularVelocityItrf93RadiansPerSecond;
            BasiliskSpacecraftState spacecraft = step.Spacecraft;

            Vector3d positionItrf93 = j2000ToItrf93.Rotate(
                spacecraft.PositionBNInertialMeters - step.Earth.PositionInertialMeters);
            Vector3d velocityItrf93 = j2000ToItrf93.Rotate(
                    spacecraft.VelocityBNInertialMetersPerSecond - step.Earth.VelocityInertialMetersPerSecond) -
                Vector3d.Cross(earthRate, positionItrf93);
            Quaterniond bodyToItrf93 = (j2000ToItrf93 * spacecraft.SigmaBN.ToQuaternion()).Normalized();

            return new SpacecraftState(
                sequence,
                BasiliskTime.ToSeconds(step.SimulationTimeNanoseconds),
                BasiliskTime.ToUtc(epochUtc, step.SimulationTimeNanoseconds),
                positionItrf93,
                velocityItrf93,
                bodyToItrf93,
                spacecraft.OmegaBNBodyRadiansPerSecond,
                ReferenceFrame.Itrf93);
        }

        // Every configured sensor must report exactly on the steps its sample period divides, at the
        // step time, with the payload type of its kind.
        public static SensorMeasurementSet MapMeasurements(
            BasiliskStepState step,
            IReadOnlyList<SensorConfiguration> sensors)
        {
            Dictionary<string, SensorConfiguration> sensorsById =
                new Dictionary<string, SensorConfiguration>(StringComparer.Ordinal);
            foreach (SensorConfiguration sensor in sensors)
            {
                sensorsById.Add(sensor.Definition.SensorId, sensor);
            }

            Dictionary<string, object> measurements = new Dictionary<string, object>(StringComparer.Ordinal);
            List<string> unavailableSensorIds = new List<string>();
            HashSet<string> sampledSensorIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (BasiliskSensorSample sample in step.SensorSamples)
            {
                if (sample.SensorId == null || !sensorsById.TryGetValue(sample.SensorId, out SensorConfiguration sensor))
                {
                    throw new ArgumentException($"Unknown sensor '{sample.SensorId}'.", nameof(step));
                }

                if (!sampledSensorIds.Add(sample.SensorId))
                {
                    throw new ArgumentException($"Sensor '{sample.SensorId}' reported twice.", nameof(step));
                }

                if (sample.SampleTimeNanoseconds != step.SimulationTimeNanoseconds)
                {
                    throw new ArgumentException(
                        $"Sensor '{sample.SensorId}' was sampled at {sample.SampleTimeNanoseconds} ns, " +
                        $"not the step time {step.SimulationTimeNanoseconds} ns.",
                        nameof(step));
                }

                if (sample.Measurement == null)
                {
                    unavailableSensorIds.Add(sample.SensorId);
                    continue;
                }

                if (!IsPayloadOfKind(sample.Measurement, sensor.Kind))
                {
                    throw new ArgumentException(
                        $"Sensor '{sample.SensorId}' is a {sensor.Kind} but reported a {sample.Measurement.GetType().Name}.",
                        nameof(step));
                }

                measurements.Add(sample.SensorId, sample.Measurement);
            }

            foreach (SensorConfiguration sensor in sensors)
            {
                long periodNanoseconds = BasiliskTime.ToNanoseconds(sensor.Definition.SamplePeriodSeconds);
                bool isDue = step.SimulationTimeNanoseconds % periodNanoseconds == 0;
                if (isDue != sampledSensorIds.Contains(sensor.Definition.SensorId))
                {
                    throw new ArgumentException(
                        $"Sensor '{sensor.Definition.SensorId}' was {(isDue ? "not sampled on" : "sampled off")} " +
                        $"its period at {step.SimulationTimeNanoseconds} ns.",
                        nameof(step));
                }
            }

            return new SensorMeasurementSet(measurements, unavailableSensorIds);
        }

        private static bool IsOrthonormal(Vector3d r0, Vector3d r1, Vector3d r2)
        {
            Vector3d[] rows = { r0, r1, r2 };
            for (int i = 0; i < rows.Length; i++)
            {
                for (int j = 0; j < rows.Length; j++)
                {
                    double expected = i == j ? 1.0 : 0.0;
                    if (Math.Abs(Vector3d.Dot(rows[i], rows[j]) - expected) > OrthonormalTolerance)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsPayloadOfKind(object measurement, SensorKind kind) => kind switch
        {
            SensorKind.Imu => measurement is ImuMeasurement,
            SensorKind.Magnetometer => measurement is MagnetometerMeasurement,
            SensorKind.LightSensor => measurement is LightSensorMeasurement,
            _ => false
        };
    }
}
