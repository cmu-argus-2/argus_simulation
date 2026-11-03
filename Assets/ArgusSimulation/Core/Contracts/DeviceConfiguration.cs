namespace Argus.Simulation.Core
{
    // One configured device of the run configuration (D11, G5): its definition, the name of its
    // registered kind, and that kind's parameters and initial state. Build one with its kind's
    // Configure, or from a run file's keys.
    public readonly struct DeviceConfiguration
    {
        private readonly DeviceValues _parameters;
        private readonly DeviceValues _initialState;

        public DeviceConfiguration(
            SensorDefinition definition,
            string kind,
            DeviceValues parameters,
            DeviceValues initialState = null)
        {
            Definition = definition;
            Kind = kind;
            _parameters = parameters;
            _initialState = initialState;
        }

        public SensorDefinition Definition { get; }
        public string Kind { get; }
        public DeviceValues Parameters => _parameters ?? DeviceValues.Empty;
        public DeviceValues InitialState => _initialState ?? DeviceValues.Empty;

        // The registered kind, or null when Kind names none.
        public IDeviceKind KindDefinition => DeviceKinds.Find(Kind);

        public bool IsValid => Definition.IsValid && FindProblem() == null;

        // False for an unregistered kind, which is never valid.
        public bool IsBackendModelled => KindDefinition?.IsBackendModelled ?? false;
        public bool MayBeControllerVisible => KindDefinition?.MayBeControllerVisible ?? false;

        // The first problem with the kind, its parameters or its initial state, or null. The
        // definition is checked by Definition.IsValid.
        public string FindProblem()
        {
            IDeviceKind kind = KindDefinition;
            return kind == null
                ? $"kind: '{Kind}' is not a registered device kind ({DeviceKinds.Names})"
                : kind.FindProblem(Parameters, InitialState);
        }
    }
}
