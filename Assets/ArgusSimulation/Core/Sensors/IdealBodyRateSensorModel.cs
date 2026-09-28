using System;

namespace Argus.Simulation.Core
{
    // Truth-backed, noise-free body-rate measurement. This is an explicit ideal sensor
    // model, not a hardware model and not a source of fabricated telemetry.
    public sealed class IdealBodyRateSensorModel : SensorModel<AngularRateMeasurement>
    {
        public IdealBodyRateSensorModel(
            string sensorId,
            string frameId,
            double samplePeriodSeconds,
            string source = "simulation")
            : base(new SensorDefinition(
                sensorId,
                "ideal-body-rate",
                frameId,
                samplePeriodSeconds,
                source,
                SensorMount.Identity))
        {
        }

        protected override SensorFrameStatus Measure(
            SensorSampleContext context,
            out AngularRateMeasurement payload)
        {
            Vector3d angularVelocity = context.Spacecraft.AngularVelocityBodyRadiansPerSecond;
            payload = new AngularRateMeasurement(angularVelocity);
            return angularVelocity.IsFinite
                ? SensorFrameStatus.Valid
                : SensorFrameStatus.Invalid;
        }
    }
}
