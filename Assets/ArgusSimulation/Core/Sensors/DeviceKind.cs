using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // A device kind whose parameters read into one typed profile. It checks the parameter map
    // against its declared keys (unknown, missing, wrongly typed or non-finite values), then the
    // profile's own IsValid, and names the key or the profile in the problem.
    public abstract class DeviceKind<TProfile> : IDeviceKind
        where TProfile : struct
    {
        private readonly DeviceKey[] _parameterKeys;

        protected DeviceKind(
            string name,
            DeviceRole role,
            bool isBackendModelled,
            bool mayBeControllerVisible,
            params DeviceKey[] parameterKeys)
        {
            Name = name;
            Role = role;
            IsBackendModelled = isBackendModelled;
            MayBeControllerVisible = mayBeControllerVisible;
            _parameterKeys = parameterKeys;
        }

        public string Name { get; }
        public DeviceRole Role { get; }
        public bool IsBackendModelled { get; }
        public bool MayBeControllerVisible { get; }
        public IReadOnlyList<DeviceKey> ParameterKeys => _parameterKeys;

        public string FindProblem(DeviceValues parameters, DeviceValues initialState)
        {
            parameters = parameters ?? DeviceValues.Empty;
            string problem = FindKeyProblem("parameters", _parameterKeys, parameters) ?? FindValueProblem(parameters);
            if (problem != null)
            {
                return problem;
            }

            // No kind has state yet; the first actuator kinds declare theirs (docs/device-model.md §6).
            if (initialState != null && initialState.Count > 0)
            {
                return $"initialState: must be empty, because the {Name} kind has no state";
            }

            return IsValid(Parse(parameters))
                ? null
                : $"parameters: is not a valid {Name} profile (see {typeof(TProfile).Name}.IsValid)";
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

        public DeviceConfiguration Configure(SensorDefinition definition, TProfile profile) =>
            new DeviceConfiguration(definition, Name, ToParameters(profile));

        public abstract ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer);

        // Reads parameters that have every declared key with its declared type.
        protected abstract TProfile Parse(DeviceValues parameters);

        protected abstract DeviceValues ToParameters(TProfile profile);

        protected abstract bool IsValid(TProfile profile);

        // A problem the declared types cannot express, checked before Parse: for example a number
        // that must be whole. Null when there is none.
        protected virtual string FindValueProblem(DeviceValues parameters) => null;

        // The first key in values that is not exactly one of the declared keys with its type and a
        // finite value, or null. The group names the map in the problem: "parameters", "output".
        protected string FindKeyProblem(string group, IReadOnlyList<DeviceKey> keys, DeviceValues values)
        {
            HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (DeviceKey key in keys)
            {
                declared.Add(key.Name);
                if (!values.TryGetValue(key.Name, out DeviceValue value))
                {
                    return $"{group}[\"{key.Name}\"]: is required";
                }

                if (value.Type != key.Type)
                {
                    return $"{group}[\"{key.Name}\"]: must be a {key.Type}, not a {value.Type}";
                }

                if (!value.IsFinite)
                {
                    return $"{group}[\"{key.Name}\"]: must be finite";
                }
            }

            foreach (string name in values.Keys)
            {
                if (!declared.Contains(name))
                {
                    return $"{group}[\"{name}\"]: is not a {Name} key";
                }
            }

            return null;
        }
    }

    // A sensor kind the backend models (D10): its frames publish what the backend measured, read
    // from the backend's output map.
    public abstract class BackendSensorKind<TProfile, TMeasurement> : DeviceKind<TProfile>, IBackendSensorKind
        where TProfile : struct
        where TMeasurement : struct
    {
        private readonly DeviceKey[] _outputKeys;

        protected BackendSensorKind(string name, DeviceKey[] outputKeys, params DeviceKey[] parameterKeys)
            : base(name, DeviceRole.Sensor, isBackendModelled: true, mayBeControllerVisible: true, parameterKeys)
        {
            _outputKeys = outputKeys;
        }

        public IReadOnlyList<DeviceKey> OutputKeys => _outputKeys;

        public object ReadOutput(DeviceValues output)
        {
            string problem = FindKeyProblem("output", _outputKeys, output ?? DeviceValues.Empty);
            if (problem != null)
            {
                throw new ArgumentException(problem, nameof(output));
            }

            return Read(output);
        }

        protected abstract TMeasurement Read(DeviceValues output);
    }
}
