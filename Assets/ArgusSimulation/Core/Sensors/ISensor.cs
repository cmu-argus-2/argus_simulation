namespace Argus.Simulation.Core
{
    public interface ISensor
    {
        SensorDefinition Definition { get; }
        void Reset(SensorResetContext context);

        // The run ended without being sampled again (a stop, a backend failure, a run that reached
        // its last step, or a Reset that replaces this sensor): release anything still in flight,
        // such as a camera's renders (CORE-21). Nothing it held is published. Idempotent, and it
        // doesn't invalidate the sensor: sampling still works until the next run calls Reset.
        void End();
        bool TrySample(SensorSampleContext context, out ISensorFrame frame);
    }
}
