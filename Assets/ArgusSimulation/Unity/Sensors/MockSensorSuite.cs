using System;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    public readonly struct MockSensorSnapshot
    {
        public MockSensorSnapshot(
            SpacecraftState state,
            double longitudeDegrees,
            double latitudeDegrees,
            double altitudeMeters,
            double speedMetersPerSecond,
            Vector3d accelerationEcef,
            Vector3d magneticFieldNanoTesla,
            Vector3d sunVectorBody,
            int gpsSatellites,
            double batteryPercent,
            double busVoltage,
            double busCurrent,
            double solarPowerWatts,
            Vector3d temperaturesCelsius,
            Vector3d reactionWheelRpm,
            double radioRssiDbm,
            double downlinkKbps,
            bool starTrackerValid)
        {
            State = state;
            LongitudeDegrees = longitudeDegrees;
            LatitudeDegrees = latitudeDegrees;
            AltitudeMeters = altitudeMeters;
            SpeedMetersPerSecond = speedMetersPerSecond;
            AccelerationEcef = accelerationEcef;
            MagneticFieldNanoTesla = magneticFieldNanoTesla;
            SunVectorBody = sunVectorBody;
            GpsSatellites = gpsSatellites;
            BatteryPercent = batteryPercent;
            BusVoltage = busVoltage;
            BusCurrent = busCurrent;
            SolarPowerWatts = solarPowerWatts;
            TemperaturesCelsius = temperaturesCelsius;
            ReactionWheelRpm = reactionWheelRpm;
            RadioRssiDbm = radioRssiDbm;
            DownlinkKbps = downlinkKbps;
            StarTrackerValid = starTrackerValid;
        }

        public SpacecraftState State { get; }
        public double LongitudeDegrees { get; }
        public double LatitudeDegrees { get; }
        public double AltitudeMeters { get; }
        public double SpeedMetersPerSecond { get; }
        public Vector3d AccelerationEcef { get; }
        public Vector3d MagneticFieldNanoTesla { get; }
        public Vector3d SunVectorBody { get; }
        public int GpsSatellites { get; }
        public double BatteryPercent { get; }
        public double BusVoltage { get; }
        public double BusCurrent { get; }
        public double SolarPowerWatts { get; }
        public Vector3d TemperaturesCelsius { get; }
        public Vector3d ReactionWheelRpm { get; }
        public double RadioRssiDbm { get; }
        public double DownlinkKbps { get; }
        public bool StarTrackerValid { get; }
    }

    public sealed class MockSensorSuite : MonoBehaviour
    {
        private const double Wgs84SemiMajorAxisMeters = 6_378_137.0;
        private const double Wgs84EccentricitySquared = 6.69437999014e-3;

        [SerializeField] private SimulationRunner runner;

        private Vector3d _previousVelocity;
        private double _previousTime;
        private bool _hasPreviousState;

        public MockSensorSnapshot Latest { get; private set; }
        public bool HasSnapshot { get; private set; }

        public void Configure(SimulationRunner simulationRunner)
        {
            if (runner != null && isActiveAndEnabled)
            {
                runner.StateProduced -= HandleState;
            }

            runner = simulationRunner;
            if (runner != null && isActiveAndEnabled)
            {
                runner.StateProduced += HandleState;
            }
        }

        private void OnEnable()
        {
            if (runner == null)
            {
                runner = GetComponent<SimulationRunner>();
            }

            if (runner != null)
            {
                runner.StateProduced += HandleState;
            }
        }

        private void OnDisable()
        {
            if (runner != null)
            {
                runner.StateProduced -= HandleState;
            }
        }

        private void HandleState(SpacecraftState state)
        {
            EcefToGeodetic(
                state.PositionEcefMeters,
                out double longitude,
                out double latitude,
                out double altitude);

            double deltaTime = state.SimulationTimeSeconds - _previousTime;
            Vector3d acceleration = _hasPreviousState && deltaTime > 0.0
                ? (state.VelocityEcefMetersPerSecond - _previousVelocity) / deltaTime
                : new Vector3d(0.0, 0.0, 0.0);

            double time = state.SimulationTimeSeconds;
            double latitudeRadians = latitude * Math.PI / 180.0;
            Vector3d magneticField = new Vector3d(
                22_000.0 * Math.Cos(latitudeRadians),
                2_500.0 * Math.Sin(time / 900.0),
                -44_000.0 * Math.Sin(latitudeRadians));
            Vector3d sunVector = new Vector3d(
                Math.Cos(time / 86164.0 * Math.PI * 2.0),
                Math.Sin(time / 86164.0 * Math.PI * 2.0),
                0.32).Normalized();

            double sunlight = Math.Max(0.0, sunVector.X);
            Latest = new MockSensorSnapshot(
                state,
                longitude,
                latitude,
                altitude,
                state.VelocityEcefMetersPerSecond.Magnitude,
                acceleration,
                magneticField,
                sunVector,
                11 + (int)Math.Round(2.0 * Math.Sin(time / 240.0)),
                Clamp(82.0 + 8.0 * Math.Sin(time / 1200.0), 0.0, 100.0),
                7.4 + 0.15 * Math.Sin(time / 180.0),
                1.1 + 0.45 * sunlight,
                18.0 * sunlight,
                new Vector3d(
                    21.0 + 4.0 * sunlight,
                    18.0 + 2.0 * Math.Sin(time / 300.0),
                    24.0 + 5.0 * sunlight),
                new Vector3d(
                    1350.0 * Math.Sin(time / 80.0),
                    980.0 * Math.Cos(time / 105.0),
                    760.0 * Math.Sin(time / 125.0)),
                -72.0 + 5.0 * Math.Sin(time / 45.0),
                256.0 + 96.0 * Math.Max(0.0, Math.Cos(time / 70.0)),
                Math.Sin(time / 500.0) < 0.94);

            _previousVelocity = state.VelocityEcefMetersPerSecond;
            _previousTime = state.SimulationTimeSeconds;
            _hasPreviousState = true;
            HasSnapshot = true;
        }

        private static void EcefToGeodetic(
            Vector3d position,
            out double longitudeDegrees,
            out double latitudeDegrees,
            out double altitudeMeters)
        {
            double longitude = Math.Atan2(position.Y, position.X);
            double horizontal = Math.Sqrt(position.X * position.X + position.Y * position.Y);
            double latitude = Math.Atan2(position.Z, horizontal * (1.0 - Wgs84EccentricitySquared));
            double altitude = 0.0;

            for (int iteration = 0; iteration < 6; iteration++)
            {
                double sine = Math.Sin(latitude);
                double normalRadius = Wgs84SemiMajorAxisMeters /
                    Math.Sqrt(1.0 - Wgs84EccentricitySquared * sine * sine);
                altitude = horizontal / Math.Cos(latitude) - normalRadius;
                latitude = Math.Atan2(
                    position.Z,
                    horizontal * (1.0 - Wgs84EccentricitySquared *
                        normalRadius / (normalRadius + altitude)));
            }

            longitudeDegrees = longitude * 180.0 / Math.PI;
            latitudeDegrees = latitude * 180.0 / Math.PI;
            altitudeMeters = altitude;
        }

        private static double Clamp(double value, double minimum, double maximum) =>
            Math.Max(minimum, Math.Min(maximum, value));
    }
}
