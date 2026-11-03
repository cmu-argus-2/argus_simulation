using System;
using System.Collections;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // An immutable map from key to DeviceValue: a device's parameters, initial state or output.
    // Keys are enumerated in ordinal order, so the same values always record the same bytes
    // (docs/device-model.md §3).
    public sealed class DeviceValues : IReadOnlyCollection<KeyValuePair<string, DeviceValue>>
    {
        public static readonly DeviceValues Empty = new DeviceValues(new SortedDictionary<string, DeviceValue>(StringComparer.Ordinal));

        private readonly SortedDictionary<string, DeviceValue> _values;

        private DeviceValues(SortedDictionary<string, DeviceValue> values)
        {
            _values = values;
        }

        public int Count => _values.Count;
        public IEnumerable<string> Keys => _values.Keys;

        public bool ContainsKey(string key) => _values.ContainsKey(key);
        public bool TryGetValue(string key, out DeviceValue value) => _values.TryGetValue(key, out value);

        public DeviceValue this[string key] => _values.TryGetValue(key, out DeviceValue value)
            ? value
            : throw new KeyNotFoundException($"No device value '{key}'.");

        public double Number(string key) => this[key].Number;
        public Vector3d Vector3(string key) => this[key].Vector3;

        public IEnumerator<KeyValuePair<string, DeviceValue>> GetEnumerator() => _values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public sealed class Builder
        {
            private readonly SortedDictionary<string, DeviceValue> _values =
                new SortedDictionary<string, DeviceValue>(StringComparer.Ordinal);

            // Throws on a blank or repeated key: a map has one value per key.
            public Builder Add(string key, DeviceValue value)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("A device value key must not be blank.", nameof(key));
                }

                if (_values.ContainsKey(key))
                {
                    throw new ArgumentException($"The device value key '{key}' is repeated.", nameof(key));
                }

                _values.Add(key, value);
                return this;
            }

            public Builder Add(string key, double value) => Add(key, DeviceValue.FromNumber(value));
            public Builder Add(string key, Vector3d value) => Add(key, DeviceValue.FromVector3(value));

            public DeviceValues Build() => new DeviceValues(new SortedDictionary<string, DeviceValue>(_values, StringComparer.Ordinal));
        }
    }
}
