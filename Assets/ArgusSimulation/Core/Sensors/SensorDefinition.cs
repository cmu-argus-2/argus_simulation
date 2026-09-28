using System;

namespace Argus.Simulation.Core
{
    public readonly struct SensorMount
    {
        public SensorMount(Vector3d positionBodyMeters, Quaterniond sensorToBody)
        {
            PositionBodyMeters = positionBodyMeters;
            SensorToBody = sensorToBody;
        }

        // Lever arm from the spacecraft body origin, expressed in body-frame meters.
        public Vector3d PositionBodyMeters { get; }

        // Rotation from the sensor measurement frame into the spacecraft body frame.
        public Quaterniond SensorToBody { get; }

        public bool IsValid => PositionBodyMeters.IsFinite && SensorToBody.IsUnit;

        public static SensorMount Identity => new SensorMount(
            new Vector3d(0.0, 0.0, 0.0),
            new Quaterniond(0.0, 0.0, 0.0, 1.0));
    }

    public readonly struct SensorDefinition
    {
        public SensorDefinition(
            string sensorId,
            string modelName,
            string frameId,
            double samplePeriodSeconds,
            string source,
            SensorMount mount)
        {
            SensorId = sensorId;
            ModelName = modelName;
            FrameId = frameId;
            SamplePeriodSeconds = samplePeriodSeconds;
            Source = source;
            Mount = mount;
        }

        public string SensorId { get; }
        public string ModelName { get; }
        public string FrameId { get; }
        public double SamplePeriodSeconds { get; }
        public string Source { get; }
        public SensorMount Mount { get; }

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(SensorId) &&
            !string.IsNullOrWhiteSpace(ModelName) &&
            !string.IsNullOrWhiteSpace(FrameId) &&
            SamplePeriodSeconds > 0.0 &&
            !double.IsNaN(SamplePeriodSeconds) &&
            !double.IsInfinity(SamplePeriodSeconds) &&
            !string.IsNullOrWhiteSpace(Source) &&
            Mount.IsValid;
    }
}
