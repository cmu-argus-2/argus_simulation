namespace Argus.Simulation.Core
{
    // One Argus IMU: a gyroscope and an accelerometer sharing a mount and sample period.
    public readonly struct ImuProfile
    {
        public ImuProfile(GyroscopeProfile gyroscope, AccelerometerProfile accelerometer)
        {
            Gyroscope = gyroscope;
            Accelerometer = accelerometer;
        }

        public GyroscopeProfile Gyroscope { get; }
        public AccelerometerProfile Accelerometer { get; }

        public bool IsValid => Gyroscope.IsValid && Accelerometer.IsValid;
    }
}
