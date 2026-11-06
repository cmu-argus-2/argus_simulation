using System;
using System.Globalization;

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

        // A sensor the backend samples, on the steps its sample period divides:
        // SimulationConfiguration.BackendSensors and the Basilisk mapper's check of that list use this
        // one condition (CORE-22).
        public bool IsBackendSensor => IsBackendModelled && KindDefinition is IBackendSensorKind;
        public bool MayBeControllerVisible => KindDefinition?.MayBeControllerVisible ?? false;

        // The first problem with the kind, its parameters, its initial state, its source or (for a
        // camera) its exposure, or null. The rest of the definition is checked by
        // Definition.IsValid. The order matters: a camera's profile is read only once the kind's
        // own checks pass, because ICameraKind.Profile throws on invalid parameters (CORE-22).
        public string FindProblem()
        {
            IDeviceKind kind = KindDefinition;
            if (kind == null)
            {
                return $"kind: '{Kind}' is not a registered device kind ({DeviceKinds.Names})";
            }

            string problem = kind.FindProblem(Parameters, InitialState);
            if (problem != null)
            {
                return problem;
            }

            // Which side models a device belongs to its kind, not to the device (D10,
            // docs/device-model.md §1), so its recorded source is the kind's.
            string source = DeviceSource.For(kind);
            if (!string.Equals(Definition.Source, source, StringComparison.Ordinal))
            {
                return $"source: must be \"{source}\", because {kind.Name} is " +
                    $"{(kind.IsBackendModelled ? "Basilisk-modelled" : "Argus-computed")} (D10)";
            }

            // One camera can't start an exposure before its last one ends, and each recorded frame
            // keeps its exposure's start and length; equal is continuous exposure.
            if (kind is ICameraKind camera)
            {
                double exposureSeconds = camera.Profile(this).ExposureDurationSeconds;
                double periodSeconds = Definition.SamplePeriodSeconds;
                if (exposureSeconds > periodSeconds)
                {
                    return $"parameters[\"{CameraProfileKind.ExposureDuration}\"]: must not exceed samplePeriodSeconds " +
                        $"({exposureSeconds.ToString("R", CultureInfo.InvariantCulture)} s > " +
                        $"{periodSeconds.ToString("R", CultureInfo.InvariantCulture)} s): " +
                        "one camera can't start an exposure before the last one ends";
                }
            }

            return null;
        }
    }
}
