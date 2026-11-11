using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // A device kind whose parameters read into one typed profile. It checks the parameter and
    // initial-state maps against its declared keys (unknown, missing, wrongly typed or non-finite
    // values), then the profile's own validity, and names the key or group in the problem.
    public abstract class DeviceKind<TProfile> : IDeviceKind
        where TProfile : struct
    {
        private readonly DeclaredKeys _parameterKeys;
        private readonly DeclaredKeys _initialStateKeys;

        // initialStateKeys: the kind's state at t = 0 (docs/device-model.md §6); none by default.
        protected DeviceKind(
            string name,
            DeviceRole role,
            bool isBackendModelled,
            bool mayBeControllerVisible,
            DeviceKey[] parameterKeys,
            DeviceKey[] initialStateKeys = null)
        {
            Name = name;
            Role = role;
            IsBackendModelled = isBackendModelled;
            MayBeControllerVisible = mayBeControllerVisible;
            _parameterKeys = new DeclaredKeys(parameterKeys);
            _initialStateKeys = new DeclaredKeys(initialStateKeys ?? Array.Empty<DeviceKey>());
        }

        public string Name { get; }
        public DeviceRole Role { get; }
        public bool IsBackendModelled { get; }
        public bool MayBeControllerVisible { get; }
        public IReadOnlyList<DeviceKey> ParameterKeys => _parameterKeys.Keys;
        public IReadOnlyList<DeviceKey> InitialStateKeys => _initialStateKeys.Keys;

        public string FindProblem(DeviceValues parameters, DeviceValues initialState)
        {
            parameters = parameters ?? DeviceValues.Empty;
            return FindKeyProblem("parameters", _parameterKeys, parameters) ??
                FindKeyProblem("initialState", _initialStateKeys, initialState ?? DeviceValues.Empty) ??
                FindValueProblem(parameters) ??
                FindProfileProblem(Parse(parameters));
        }

        // The profile of a valid configuration of this kind.
        public TProfile Profile(DeviceConfiguration configuration)
        {
            if (configuration.Kind != Name || FindProblem(configuration.Parameters, configuration.InitialState) != null)
            {
                throw new ArgumentException($"A valid {Name} device configuration is required.", nameof(configuration));
            }

            return Parse(configuration.Parameters);
        }

        // Which side models the device belongs to its kind, not to the device (docs/device-model.md
        // §1), so the definition's source is replaced by this kind's (DeviceSource.For, CORE-22).
        public DeviceConfiguration Configure(SensorDefinition definition, TProfile profile) =>
            new DeviceConfiguration(
                new SensorDefinition(
                    definition.SensorId,
                    definition.ModelName,
                    definition.FrameId,
                    definition.SamplePeriodSeconds,
                    DeviceSource.For(this),
                    definition.Mount,
                    definition.IsControllerVisible),
                Name,
                ToParameters(profile));

        public abstract ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer);

        // Reads parameters that have every declared key with its declared type.
        protected abstract TProfile Parse(DeviceValues parameters);

        protected abstract DeviceValues ToParameters(TProfile profile);

        protected abstract bool IsValid(TProfile profile);

        // A problem the declared types cannot express, checked before Parse: for example a number
        // that must be whole. Null when there is none.
        protected virtual string FindValueProblem(DeviceValues parameters) => null;

        // Why a parsed profile is invalid, naming the narrowest part the kind can (a group such as
        // gyroscope.*), or null. The default names the whole profile.
        protected virtual string FindProfileProblem(TProfile profile) =>
            IsValid(profile) ? null : $"parameters: is not a valid {Name} profile (see {typeof(TProfile).Name}.IsValid)";

        // The first key in values that is not exactly one of the declared keys with its type and a
        // set, finite value, or null. The group names the map in the problem: "parameters", "output".
        protected string FindKeyProblem(string group, DeclaredKeys keys, DeviceValues values)
        {
            foreach (DeviceKey key in keys.Keys)
            {
                if (!values.TryGetValue(key.Name, out DeviceValue value))
                {
                    return $"{group}[\"{key.Name}\"]: is required";
                }

                if (value.Type != key.Type)
                {
                    return $"{group}[\"{key.Name}\"]: must be a {key.Type}, not a {value.Type}";
                }

                if (value.Type == DeviceValueType.Text && value.Text == null)
                {
                    return $"{group}[\"{key.Name}\"]: must not be null";
                }

                if (!value.IsFinite)
                {
                    return $"{group}[\"{key.Name}\"]: must be finite";
                }
            }

            foreach (string name in values.Keys)
            {
                if (!keys.Contains(name))
                {
                    return $"{group}[\"{name}\"]: is not a {Name} key";
                }
            }

            return null;
        }

        // A kind's declared keys and their names, built once: outputs are checked on every sample.
        protected sealed class DeclaredKeys
        {
            private readonly HashSet<string> _names = new HashSet<string>(StringComparer.Ordinal);

            public DeclaredKeys(DeviceKey[] keys)
            {
                Keys = keys;
                foreach (DeviceKey key in keys)
                {
                    _names.Add(key.Name);
                }
            }

            public IReadOnlyList<DeviceKey> Keys { get; }

            public bool Contains(string name) => _names.Contains(name);
        }
    }

    // A sensor kind the backend models (D10): its frames publish what the backend measured, read
    // from the backend's output map.
    public abstract class BackendSensorKind<TProfile, TMeasurement> : DeviceKind<TProfile>, IBackendSensorKind
        where TProfile : struct
        where TMeasurement : struct
    {
        private readonly DeclaredKeys _outputKeys;

        protected BackendSensorKind(string name, DeviceKey[] outputKeys, params DeviceKey[] parameterKeys)
            : base(name, DeviceRole.Sensor, isBackendModelled: true, mayBeControllerVisible: true, parameterKeys)
        {
            _outputKeys = new DeclaredKeys(outputKeys);
        }

        public IReadOnlyList<DeviceKey> OutputKeys => _outputKeys.Keys;

        public object ReadOutput(DeviceValues output)
        {
            string problem = FindKeyProblem("output", _outputKeys, output ?? DeviceValues.Empty);
            if (problem != null)
            {
                throw new ArgumentException(problem, nameof(output));
            }

            return Read(output);
        }

        public DeviceValues WriteOutput(object measurement) => measurement is TMeasurement typed
            ? Write(typed)
            : throw new ArgumentException(
                $"A {Name} output is a {typeof(TMeasurement).Name}, not a {measurement?.GetType().Name ?? "null"}.",
                nameof(measurement));

        protected abstract TMeasurement Read(DeviceValues output);

        protected abstract DeviceValues Write(TMeasurement measurement);
    }
}
