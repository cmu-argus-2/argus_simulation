using Argus.Simulation.Core;

namespace Argus.Simulation.Tests
{
    // The IMU profiles the tests share (CORE-22): a generic placeholder part, and P0-like values for
    // tests that record or step a P0-shaped run. Values only: each test keeps its own
    // SensorDefinition, whose source is the IMU kind's (DeviceSource.Basilisk).
    internal static class TestImu
    {
        private static readonly Vector3d Zero = new Vector3d(0.0, 0.0, 0.0);
        private static readonly Vector3d UnitScale = new Vector3d(1.0, 1.0, 1.0);

        public static ImuProfile Profile() =>
            new ImuProfile(
                new GyroscopeProfile(1e-4, 0.0, 0.0, Zero, UnitScale, 4.0, 1e-4),
                new AccelerometerProfile(1e-3, 0.0, 0.0, Zero, UnitScale, 20.0, 1e-3));

        public static ImuProfile P0LikeProfile() =>
            new ImuProfile(
                new GyroscopeProfile(1.2e-4, 0.0, 0.0, Zero, UnitScale, 4.363, 1.33e-4),
                new AccelerometerProfile(1.8e-3, 0.0, 0.0, Zero, UnitScale, 19.61, 6.0e-4));
    }
}
