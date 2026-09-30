namespace Argus.Simulation.Core
{
    // PLACEHOLDER: not implemented, not registered, not referenced.
    // TODO(sensors): fine Sun sensor (LightSensor already covers coarse Sun sensing).
    // - Measurement: Sun direction in sensor axes, validity (Sun in field of view, not eclipsed).
    // - Profile: field of view, angular noise, bias, output rate.
    // - Source: bsk 2.11.1 has only coarseSunSensor, so this is likely an Argus-computed
    //   SensorModel<T> from EnvironmentState (Sun position, shadow factor). That needs
    //   SensorSampleContext.Environment first (target-architecture §5). Never approximate the Sun.
    internal sealed class SunSensor
    {
    }
}
