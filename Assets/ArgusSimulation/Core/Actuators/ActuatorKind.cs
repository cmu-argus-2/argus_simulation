using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // An actuator kind whose parameters read into one typed profile (D11, CON-2). Each kind's axis
    // is a body-frame unit-vector parameter, so an actuator's mount never rotates
    // (SimulationConfiguration checks it), and its limit_policy says whether the gateway clamps or
    // rejects a command past its limits (CORE-10, #46).
    public abstract class ActuatorKind<TProfile> : DeviceKind<TProfile>, IActuatorKind
        where TProfile : struct
    {
        public const string LimitPolicyKey = "limit_policy";

        private const string ClampText = "clamp";
        private const string RejectText = "reject";

        private readonly DeclaredKeys _commandKeys;

        protected ActuatorKind(
            string name,
            DeviceKey[] commandKeys,
            DeviceValues idleValues,
            DeviceKey[] parameterKeys,
            DeviceKey[] initialStateKeys = null)
            : base(name, DeviceRole.Actuator, isBackendModelled: true, mayBeControllerVisible: false, parameterKeys, initialStateKeys)
        {
            _commandKeys = new DeclaredKeys(commandKeys);
            IdleValues = idleValues;
        }

        public IReadOnlyList<DeviceKey> CommandKeys => _commandKeys.Keys;
        public DeviceValues IdleValues { get; }

        public string FindCommandProblem(DeviceValues values)
        {
            values = values ?? DeviceValues.Empty;
            return FindKeyProblem("values", _commandKeys, values) ?? FindCommandValueProblem(values);
        }

        public bool IsIdle(DeviceValues values)
        {
            foreach (DeviceKey key in _commandKeys.Keys)
            {
                if (!values.TryGetValue(key.Name, out DeviceValue value) || !IsSame(value, IdleValues[key.Name]))
                {
                    return false;
                }
            }

            return true;
        }

        public override ISensor CreateSensor(DeviceConfiguration configuration, IImageRenderer imageRenderer) =>
            throw new InvalidOperationException($"Device kind {Name} is an actuator, not a sensor.");

        // A command value the kind can never take whatever its limits, checked after the keys;
        // null when there is none. Limits are CORE-10's.
        protected virtual string FindCommandValueProblem(DeviceValues values) => null;

        protected override string FindValueProblem(DeviceValues parameters) =>
            ParseLimitPolicy(parameters[LimitPolicyKey].Text) == ActuatorLimitPolicy.Unknown
                ? $"parameters[\"{LimitPolicyKey}\"]: must be {ClampText} or {RejectText}"
                : null;

        protected static ActuatorLimitPolicy LimitPolicy(DeviceValues parameters) =>
            ParseLimitPolicy(parameters[LimitPolicyKey].Text);

        protected static DeviceValue ToValue(ActuatorLimitPolicy policy) => DeviceValue.FromText(
            policy == ActuatorLimitPolicy.Clamp ? ClampText : policy == ActuatorLimitPolicy.Reject ? RejectText : null);

        // The first of the named problems, for FindProfileProblem.
        protected static string FirstProblem(params (bool IsValid, string Key, string Rule)[] checks)
        {
            foreach ((bool isValid, string key, string rule) in checks)
            {
                if (!isValid)
                {
                    return $"parameters[\"{key}\"]: {rule}";
                }
            }

            return null;
        }

        protected const string Positive = "must be > 0";
        protected const string PositiveComponents = "each component must be > 0";
        protected const string UnitAxis = "must have unit length within 1e-6 (ActuatorGeometry.UnitTolerance)";

        private static bool IsSame(DeviceValue value, DeviceValue idle) =>
            value.Type == idle.Type && value.Type switch
            {
                DeviceValueType.Number => value.Number == idle.Number,
                DeviceValueType.Vector3 => value.Vector3.Equals(idle.Vector3),
                DeviceValueType.Flag => value.Flag == idle.Flag,
                DeviceValueType.Text => value.Text == idle.Text,
                _ => false
            };

        private static ActuatorLimitPolicy ParseLimitPolicy(string text) =>
            text == ClampText ? ActuatorLimitPolicy.Clamp :
            text == RejectText ? ActuatorLimitPolicy.Reject :
            ActuatorLimitPolicy.Unknown;
    }
}
