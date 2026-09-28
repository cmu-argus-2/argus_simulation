namespace Argus.Simulation.Core
{
    public interface ISensor
    {
        SensorDefinition Definition { get; }
        void Reset(SensorResetContext context);
        bool TrySample(SensorSampleContext context, out ISensorFrame frame);
    }
}
