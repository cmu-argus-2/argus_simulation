using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // The device kind registry (D11, docs/device-model.md §7). Adding a kind means writing its
    // IDeviceKind and one line here; the gateway, the recorder, the run file and the wire do not
    // change.
    public static class DeviceKinds
    {
        private static readonly IDeviceKind[] Registered =
        {
            ImuKind.Instance,
            MagnetometerKind.Instance,
            LightSensorKind.Instance,
            CameraKind.Instance
        };

        private static readonly Dictionary<string, IDeviceKind> ByName = Index(Registered);

        public static IReadOnlyList<IDeviceKind> All => Registered;

        // The registered names, for error messages.
        public static string Names => string.Join(", ", Array.ConvertAll(Registered, kind => kind.Name));

        // The kind registered under this name (ordinal), or null.
        public static IDeviceKind Find(string name) =>
            name != null && ByName.TryGetValue(name, out IDeviceKind kind) ? kind : null;

        private static Dictionary<string, IDeviceKind> Index(IDeviceKind[] kinds)
        {
            Dictionary<string, IDeviceKind> byName = new Dictionary<string, IDeviceKind>(StringComparer.Ordinal);
            foreach (IDeviceKind kind in kinds)
            {
                byName.Add(kind.Name, kind);
            }

            return byName;
        }
    }
}
