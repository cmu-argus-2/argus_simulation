using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class AttitudeOverrideCommandTests
    {
        [Test]
        public void ApplyDelta_RequiresUnitQuaternion()
        {
            AttitudeOverrideCommand command = AttitudeOverrideCommand.ApplyDelta(
                0,
                0.0,
                new Quaterniond(0.0, 0.0, 0.0, 2.0));

            Assert.That(command.IsValid, Is.False);
        }

        [Test]
        public void AnalyticEngine_AppliesOverrideBeforePublishingTruth()
        {
            Quaterniond offset = Quaterniond.FromAxisAngle(
                new Vector3d(0.0, 0.0, 1.0),
                System.Math.PI / 2.0);
            AnalyticSimulationEngine baseline = BuildEngine(Quaterniond.Identity);
            AnalyticSimulationEngine overridden = BuildEngine(offset);
            SimulationStepInput input = new SimulationStepInput(
                0,
                0.0,
                ActuatorCommandSet.None(0, 0.0));

            Assert.That(baseline.TryStep(input, out SimulationSnapshot baselineSnapshot), Is.True);
            Assert.That(overridden.TryStep(input, out SimulationSnapshot overriddenSnapshot), Is.True);

            Quaterniond expected =
                (baselineSnapshot.Spacecraft.BodyToEcef * offset).Normalized();
            Quaterniond actual = overriddenSnapshot.Spacecraft.BodyToEcef;
            double dot = expected.X * actual.X + expected.Y * actual.Y +
                expected.Z * actual.Z + expected.W * actual.W;
            Assert.That(System.Math.Abs(System.Math.Abs(dot) - 1.0), Is.LessThan(1e-12));
        }

        private static AnalyticSimulationEngine BuildEngine(Quaterniond attitudeOverride)
        {
            AnalyticSimulationEngine engine = new AnalyticSimulationEngine(
                500_000.0,
                51.6,
                0.0,
                0.0,
                null,
                attitudeOverride);
            engine.Initialize(new SimulationConfiguration(
                "attitude-command-test",
                System.DateTimeOffset.UnixEpoch,
                0.1));
            return engine;
        }
    }
}
