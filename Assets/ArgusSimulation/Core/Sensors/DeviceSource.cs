using System;

namespace Argus.Simulation.Core
{
    // Who computes a configured device's frames, recorded as `source` in each frame and in the
    // dataset manifest's devices (D10). It belongs to the device's kind and is not configured per
    // device (docs/device-model.md §1), so it is derived from IDeviceKind.IsBackendModelled:
    // DeviceKind.Configure and the run file set it, and DeviceConfiguration.FindProblem rejects any
    // other (CORE-22).
    // Sensors built outside a SimulationConfiguration, such as Unity's analytic bridge sensors, keep
    // their own label ("simulation").
    public static class DeviceSource
    {
        public const string Basilisk = "basilisk";
        public const string Argus = "argus";

        public static string For(IDeviceKind kind)
        {
            if (kind == null)
            {
                throw new ArgumentNullException(nameof(kind));
            }

            return kind.IsBackendModelled ? Basilisk : Argus;
        }
    }
}
