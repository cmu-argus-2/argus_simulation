namespace Argus.Simulation.Core
{
    // One configured actuator's command values: exactly its kind's command keys (D11).
    public readonly struct DeviceCommand
    {
        private readonly DeviceValues _values;

        public DeviceCommand(string deviceId, DeviceValues values)
        {
            DeviceId = deviceId;
            _values = values;
        }

        public string DeviceId { get; }
        public DeviceValues Values => _values ?? DeviceValues.Empty;
    }
}
