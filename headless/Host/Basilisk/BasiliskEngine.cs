using System;
using System.Collections.Generic;
using System.Threading;
using Argus.Simulation.Core;
using Grpc.Core;
using Grpc.Net.Client;
using PbBasilisk = Argus.Contracts.Basilisk.V1;
using PbSim = Argus.Contracts.Sim.V1;

namespace Argus.Simulation.Host
{
    // ISimulationEngine over the Argus.Basilisk gRPC service. Basilisk owns time and pacing (D2) and
    // SPICE (D3); this adapter only maps its responses to Argus contracts through Core/Basilisk and
    // never substitutes data when the service fails.
    public sealed class BasiliskEngine : ISimulationEngine, IDisposable
    {
        private static readonly TimeSpan ConfigureRunDeadline = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan ResetDeadline = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StepDeadlineMargin = TimeSpan.FromSeconds(30);
        private const double MaximumPacedStepSeconds = 3600.0;

        private readonly GrpcChannel _channel;
        private readonly PbBasilisk.BasiliskSimulationService.BasiliskSimulationServiceClient _client;
        private readonly double _realTimeFactor;
        private readonly CancellationToken _cancellation;

        private SimulationConfiguration _configuration;
        private long _fixedStepNanoseconds;
        private bool _hasStepped;

        // realTimeFactor 0 runs unpaced; above 0 the service paces Step with clockSynch.
        public BasiliskEngine(Uri serviceAddress, double realTimeFactor, CancellationToken cancellation)
        {
            if (serviceAddress == null || serviceAddress.Scheme != Uri.UriSchemeHttp)
            {
                throw new ArgumentException("The v1 Basilisk link is plaintext HTTP/2 (http://host:port).", nameof(serviceAddress));
            }

            if (double.IsNaN(realTimeFactor) || double.IsInfinity(realTimeFactor) || realTimeFactor < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(realTimeFactor), "The real-time factor must be finite and non-negative.");
            }

            _channel = GrpcChannel.ForAddress(serviceAddress);
            _client = new PbBasilisk.BasiliskSimulationService.BasiliskSimulationServiceClient(_channel);
            _realTimeFactor = realTimeFactor;
            _cancellation = cancellation;
        }

        public string BackendName => "basilisk";
        public bool IsInitialized { get; private set; }
        public string BasiliskVersion { get; private set; }

        // clockSynch overruns since ConfigureRun or Reset; 0 when unpaced.
        public ulong PacingOverrunCount { get; private set; }

        public void Initialize(SimulationConfiguration configuration)
        {
            IsInitialized = false;
            if (!configuration.IsValid ||
                !configuration.InitialOrbit.HasValue ||
                !configuration.Spacecraft.HasValue ||
                configuration.KernelSetId == null)
            {
                throw new ArgumentException(
                    "Basilisk needs a valid configuration with InitialOrbit, Spacecraft and KernelSetId.",
                    nameof(configuration));
            }

            long fixedStepNanoseconds = BasiliskTime.ToNanoseconds(configuration.FixedStepSeconds);
            if (fixedStepNanoseconds <= 0)
            {
                throw new ArgumentException("The fixed step must be at least 1 ns.", nameof(configuration));
            }

            if (_realTimeFactor > 0.0 && configuration.FixedStepSeconds / _realTimeFactor > MaximumPacedStepSeconds)
            {
                throw new ArgumentException(
                    $"Real-time factor {_realTimeFactor:R} would pace each step to over {MaximumPacedStepSeconds} s.",
                    nameof(configuration));
            }

            // Basilisk samples only the backend-modelled sensors; Argus-computed ones schedule
            // themselves in SensorModel (CORE-18).
            foreach (SensorConfiguration sensor in configuration.Sensors)
            {
                if (sensor.IsBackendModelled &&
                    BasiliskTime.ToNanoseconds(sensor.Definition.SamplePeriodSeconds) % fixedStepNanoseconds != 0)
                {
                    throw new ArgumentException(
                        $"Sensor '{sensor.Definition.SensorId}' period is not a multiple of the fixed step.",
                        nameof(configuration));
                }
            }

            PbBasilisk.ConfigureRunRequest request = new PbBasilisk.ConfigureRunRequest
            {
                Configuration = BasiliskProtoMapper.ToProto(configuration, fixedStepNanoseconds),
                RealTimeFactor = _realTimeFactor
            };
            PbBasilisk.ConfigureRunResponse response = Call(
                "ConfigureRun",
                options => _client.ConfigureRun(request, options),
                ConfigureRunDeadline);

            Expect(response.RunId == configuration.RunId, "ConfigureRun", "run_id");
            Expect(response.KernelSetId == configuration.KernelSetId, "ConfigureRun", "kernel_set_id");
            Expect(response.FixedStepNs == (ulong)fixedStepNanoseconds, "ConfigureRun", "fixed_step_ns");
            Expect(HasSensorsInOrder(response.ConfiguredSensorIds, request.Configuration.Sensors), "ConfigureRun", "configured_sensor_ids");
            Expect(response.SpiceEarthFrame == "ITRF93", "ConfigureRun", "spice_earth_frame");
            Expect(!string.IsNullOrWhiteSpace(response.BasiliskVersion), "ConfigureRun", "basilisk_version");

            _configuration = configuration;
            _fixedStepNanoseconds = fixedStepNanoseconds;
            BasiliskVersion = response.BasiliskVersion;
            PacingOverrunCount = 0;
            _hasStepped = false;
            IsInitialized = true;
        }

        // ConfigureRun leaves the service READY; after any step, Reset rebuilds it first. The
        // gateway always calls Initialize before TryReset, so it never needs the Reset RPC; a
        // direct engine user that reruns without Initialize does (ISimulationEngine.TryReset). Then
        // Step(0) runs the t = 0 pass with a zero command, since a Step(n) command acts over
        // [t_n-1, t_n) (target-architecture §4).
        public bool TryReset(out SimulationState state)
        {
            if (!IsInitialized)
            {
                throw new InvalidOperationException("BasiliskEngine has not been initialized.");
            }

            if (_hasStepped)
            {
                PbBasilisk.ResetRequest request = new PbBasilisk.ResetRequest { RunId = _configuration.RunId };
                PbBasilisk.ResetResponse response = Call("Reset", options => _client.Reset(request, options), ResetDeadline);
                Expect(response.RunId == _configuration.RunId, "Reset", "run_id");
                PacingOverrunCount = 0;
            }

            state = CallStep(0, ActuatorCommandSet.None(0, 0.0), appliedCommands: null);
            return true;
        }

        // Command k travels in Step(k + 1), whose command acts over [t_k, t_k+1).
        public bool TryStep(SimulationStepInput input, out SimulationState state)
        {
            state = default;
            if (!IsInitialized || !input.IsValid)
            {
                return false;
            }

            long commandTimeNanoseconds = checked(input.Sequence * _fixedStepNanoseconds);
            if (!SimulationTime.AreSame(BasiliskTime.ToSeconds(commandTimeNanoseconds), input.SimulationTimeSeconds))
            {
                throw new InvalidOperationException(
                    $"Command {input.Sequence} at {input.SimulationTimeSeconds:R} s is off the {_fixedStepNanoseconds} ns grid.");
            }

            state = CallStep(checked(input.Sequence + 1), input.Commands, input.Commands);
            return true;
        }

        private SimulationState CallStep(long sequence, ActuatorCommandSet command, ActuatorCommandSet? appliedCommands)
        {
            _hasStepped = true;
            PbBasilisk.StepRequest request = new PbBasilisk.StepRequest
            {
                RunId = _configuration.RunId,
                Sequence = (ulong)sequence,
                SimTimeNs = (ulong)checked(sequence * _fixedStepNanoseconds),
                // TODO(G1): the service rejects non-zero commands until Basilisk actuators exist.
                Command = BasiliskProtoMapper.ToProto(command)
            };
            // Step(n) carries command n-1 and names its step, so the service rejects a command sent
            // in the wrong request; Step(0) follows no command and names none.
            if (appliedCommands.HasValue)
            {
                request.CommandSequence = checked((ulong)appliedCommands.Value.Sequence);
                request.CommandTimeNs = CommandTimeNanoseconds(appliedCommands.Value.Sequence, _fixedStepNanoseconds);
            }

            PbBasilisk.StepResponse response = Call("Step", options => _client.Step(request, options), StepDeadline());
            Expect(response.RunId == _configuration.RunId, "Step", "run_id");
            Expect(response.Sequence == request.Sequence, "Step", "sequence");
            Expect(response.SimTimeNs == request.SimTimeNs, "Step", "sim_time_ns");

            BasiliskStepState step = BasiliskProtoMapper.ToStepState(response);
            EnvironmentState environment = BasiliskStateMapper.MapEnvironment(step);
            SpacecraftState spacecraft = BasiliskStateMapper.MapSpacecraft(
                sequence,
                _configuration.EpochUtc,
                step,
                environment);
            SensorMeasurementSet measurements = BasiliskStateMapper.MapMeasurements(step, _configuration.Sensors);
            SimulationState state = new SimulationState(
                _configuration.RunId,
                BackendName,
                spacecraft,
                appliedCommands,
                environment: environment,
                sensorMeasurements: measurements);
            PacingOverrunCount = response.PacingOverrunCount;
            if (!state.IsValid)
            {
                throw new InvalidOperationException($"Basilisk step {sequence} mapped to an invalid state.");
            }

            return state;
        }

        // t_k in exact nanoseconds. TryStep has checked that the command's time is on this grid;
        // converting its seconds back to nanoseconds instead fails once k x step picks up rounding
        // (8192.300000000001 s at k = 81923 with a 0.1 s step).
        internal static ulong CommandTimeNanoseconds(long commandSequence, long fixedStepNanoseconds) =>
            checked((ulong)(commandSequence * fixedStepNanoseconds));

        public void Dispose()
        {
            _channel.Dispose();
        }

        // A paced step can take step / factor of wall time before the service answers.
        private TimeSpan StepDeadline()
        {
            if (_realTimeFactor <= 0.0)
            {
                return StepDeadlineMargin;
            }

            return StepDeadlineMargin + TimeSpan.FromSeconds(_configuration.FixedStepSeconds / _realTimeFactor);
        }

        private T Call<T>(string rpc, Func<CallOptions, T> call, TimeSpan deadline)
        {
            try
            {
                return call(new CallOptions(deadline: DateTime.UtcNow + deadline, cancellationToken: _cancellation));
            }
            catch (RpcException exception) when (_cancellation.IsCancellationRequested)
            {
                // Cancellation, not a backend failure: the gateway ends the run Stopped and lets it
                // through, and the host exits as cancelled.
                throw new OperationCanceledException($"Basilisk {rpc} was cancelled.", exception, _cancellation);
            }
            catch (RpcException exception)
            {
                throw new InvalidOperationException(
                    $"Basilisk {rpc} failed: {exception.StatusCode}: {exception.Status.Detail}",
                    exception);
            }
        }

        // The service echoes every requested sensor in request order, and the request carries only the
        // backend-modelled ones (BasiliskProtoMapper, CORE-18).
        private static bool HasSensorsInOrder(IReadOnlyList<string> configuredIds, IReadOnlyList<PbSim.SensorConfiguration> requested)
        {
            if (configuredIds.Count != requested.Count)
            {
                return false;
            }

            for (int i = 0; i < requested.Count; i++)
            {
                if (!string.Equals(configuredIds[i], requested[i].Definition.SensorId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static void Expect(bool condition, string rpc, string field)
        {
            if (!condition)
            {
                throw new InvalidOperationException($"Basilisk {rpc} returned an unexpected {field}.");
            }
        }
    }
}
