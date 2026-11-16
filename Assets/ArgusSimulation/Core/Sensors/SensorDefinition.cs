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
            SensorMount mount,
            bool isControllerVisible = true)
        {
            SensorId = sensorId;
            ModelName = modelName;
            FrameId = frameId;
            SamplePeriodSeconds = samplePeriodSeconds;
            Source = source;
            Mount = mount;
            IsControllerVisible = isControllerVisible;
        }

        public string SensorId { get; }
        public string ModelName { get; }
        public string FrameId { get; }
        public double SamplePeriodSeconds { get; }
        // Who computes the frames, recorded with each frame and in the manifest. For a configured
        // device it is its kind's (DeviceSource, CORE-22); other sensors, such as Unity's analytic
        // bridge sensors, carry their own label.
        public string Source { get; }
        public SensorMount Mount { get; }

        // False for ground-truth sensors (for example the nadir camera): the gateway never forwards
        // their frames to a controller, while the recorder still receives them (D7). It must be false
        // for a kind whose frames carry truth, such as a camera (IDeviceKind.MayBeControllerVisible).
        public bool IsControllerVisible { get; }

        // A valid sensor definition: the common fields and a positive sample period.
        public bool IsValid =>
            HasValidCommonFields &&
            SamplePeriodSeconds > 0.0 &&
            !double.IsNaN(SamplePeriodSeconds) &&
            !double.IsInfinity(SamplePeriodSeconds);

        // The fields every device needs. An actuator has no sample period: it takes a command
        // every step (DeviceConfiguration.IsValid, docs/device-model.md §1).
        public bool HasValidCommonFields =>
            !string.IsNullOrWhiteSpace(SensorId) &&
            !string.IsNullOrWhiteSpace(ModelName) &&
            !string.IsNullOrWhiteSpace(FrameId) &&
            !string.IsNullOrWhiteSpace(Source) &&
            Mount.IsValid;
    }
}
