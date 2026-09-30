using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    // Inputs are one literal SPICE sample (J20002Pfix, its rate and the Sun) at 2025-01-15T00:00Z, so
    // no kernels are needed (D3). Expected values were computed independently of Quaterniond.
    public sealed class BasiliskStateMapperTests
    {
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero);

        private static readonly Matrix3d J2000ToItrf93 = new Matrix3d(
            new Vector3d(-0.41275331697402873, 0.9108423308589297, 0.0009734692644210158),
            new Vector3d(-0.9108395986434633, -0.4127544635018774, 0.0022312333567098),
            new Vector3d(0.0024341055752874208, 3.42746148280378e-05, 0.9999970369732598));

        private static readonly Matrix3d J2000ToItrf93Rate = new Matrix3d(
            new Vector3d(-6.641947228340686e-05, -3.0098530839345522e-05, 1.62807911771243e-07),
            new Vector3d(3.0098447352340842e-05, -6.641967177075282e-05, -7.093902117028215e-08),
            new Vector3d(8.609191046469471e-11, -7.49538065266115e-11, -2.0698839971282307e-13));

        private static readonly Vector3d SunFromEarthJ2000 =
            new Vector3d(61571146677.18358, -122621156753.13065, -53154573837.481125);

        // Earth is off the inertial origin so the mapper must subtract its state.
        private static readonly Vector3d EarthPosition = new Vector3d(1e9, -2e9, 5e8);
        private static readonly Vector3d EarthVelocity = new Vector3d(1e4, 2e4, -3e3);

        [Test]
        public void MapsSpiceEnvironmentAndTranslation()
        {
            BasiliskStepState step = Step(0, new Mrp(0.0, 0.0, 0.0), new Vector3d(0.0, 0.0, 0.0));

            EnvironmentState environment = BasiliskStateMapper.MapEnvironment(step);
            SpacecraftState state = BasiliskStateMapper.MapSpacecraft(0, Epoch, step, environment);

            AssertNear(state.PositionEcefMeters, new Vector3d(-2838973.861351795, -6264879.544494755, 16742.111619290696), 1e-6);
            AssertNear(state.VelocityEcefMetersPerSecond, new Vector3d(3855.9331965502233, -1731.3993717720675, 5966.096202809694), 1e-9);
            AssertNear(environment.SunPositionItrf93Meters, new Vector3d(-137153979594.43587, -5587609016.053531, -53008748460.57119), 1e-3);
            AssertNear(
                environment.EarthAngularVelocityItrf93RadiansPerSecond,
                new Vector3d(4.747886481411619e-11, -1.0380602294940212e-10, 7.292115168796117e-05),
                1e-15);
            Assert.That(state.EarthFixedFrame, Is.EqualTo(ReferenceFrame.Itrf93));
            Assert.That(state.TimestampUtc, Is.EqualTo(Epoch));
            SimulationSnapshot snapshot = new SimulationSnapshot(
                "run-1",
                "basilisk",
                state,
                ActuatorCommandSet.None(0, 0.0),
                environment: environment);
            Assert.That(snapshot.IsValid, Is.True);
        }

        [Test]
        public void MapsAttitudeIncludingShadowSetAndTime()
        {
            Vector3d bodyRate = new Vector3d(0.01, -0.02, 0.03);
            long nanoseconds = 1_234_500_000_100;
            Mrp sigma = new Mrp(0.1, -0.2, 0.3);
            Mrp shadowSet = new Mrp(-0.7142857142857143, 1.4285714285714286, -2.142857142857143);

            foreach (Mrp attitude in new[] { sigma, shadowSet })
            {
                BasiliskStepState step = Step(nanoseconds, attitude, bodyRate);
                EnvironmentState environment = BasiliskStateMapper.MapEnvironment(step);
                SpacecraftState state = BasiliskStateMapper.MapSpacecraft(12345, Epoch, step, environment);

                AssertNear(state.BodyToEcef.Rotate(new Vector3d(1.0, 0.0, 0.0)), new Vector3d(0.5293991465834958, -0.4572986052835131, 0.71457296982353), 1e-12);
                AssertNear(state.BodyToEcef.Rotate(new Vector3d(0.0, 1.0, 0.0)), new Vector3d(0.7286291093491861, 0.676519870637856, -0.10686667226572621), 1e-12);
                AssertNear(state.BodyToEcef.Rotate(new Vector3d(0.0, 0.0, 1.0)), new Vector3d(-0.4345528329279163, 0.5772337916632153, 0.6913501176368094), 1e-12);
                Assert.That(state.AngularVelocityBodyRadiansPerSecond, Is.EqualTo(bodyRate));
                Assert.That(state.SimulationTimeSeconds, Is.EqualTo(1234.5000001).Within(1e-9));
                Assert.That(state.TimestampUtc, Is.EqualTo(Epoch.AddTicks(12_345_000_001)));
            }

            Assert.That(BasiliskTime.ToNanoseconds(0.1), Is.EqualTo(100_000_000));
            Assert.Throws<ArgumentException>(() => BasiliskTime.ToNanoseconds(0.1000000001));

            // Past 2^23 s one ulp exceeds 1 ns: step 83,886,082 at 0.1 s is still the same instant.
            Assert.That(SimulationTime.AreSame(83_886_082 * 0.1, BasiliskTime.ToSeconds(8_388_608_200_000_000)), Is.True);
            Assert.That(SimulationTime.AreSame(0.1, 0.1 + 1e-6), Is.False);
        }

        private static BasiliskStepState Step(long nanoseconds, Mrp sigmaBN, Vector3d omegaBNBody)
        {
            BasiliskSpacecraftState spacecraft = new BasiliskSpacecraftState(
                new Vector3d(6_878_137.0, 0.0, 0.0) + EarthPosition,
                new Vector3d(0.0, 4728.554668926528, 5965.951218540759) + EarthVelocity,
                sigmaBN,
                omegaBNBody);
            BasiliskPlanetState earth = new BasiliskPlanetState(
                "earth",
                EarthPosition,
                EarthVelocity,
                J2000ToItrf93,
                J2000ToItrf93Rate,
                true);
            BasiliskPlanetState sun = new BasiliskPlanetState(
                "sun",
                SunFromEarthJ2000 + EarthPosition,
                new Vector3d(0.0, 0.0, 0.0),
                J2000ToItrf93,
                J2000ToItrf93Rate,
                true);
            return new BasiliskStepState(nanoseconds, spacecraft, earth, sun, 1.0, null);
        }

        private static void AssertNear(Vector3d actual, Vector3d expected, double tolerance)
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(tolerance));
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(tolerance));
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(tolerance));
        }
    }
}
