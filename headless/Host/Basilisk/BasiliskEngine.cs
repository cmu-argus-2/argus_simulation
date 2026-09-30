using System;
using System.Collections.Generic;
using System.Threading;
using Argus.Simulation.Core;
using Grpc.Core;
using Grpc.Net.Client;
using PbBasilisk = Argus.Contracts.Basilisk.V1;

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
        private const double TimeToleranceSeconds = 1e-9;

        private readonly GrpcChannel _channel;
        private readonly PbBasilisk.BasiliskSimulationService.BasiliskSimulationServiceClient _client;
        private readonly double _realTimeFactor;
        private readonly CancellationToken _cancellation;

        private SimulationConfiguration _configuration;
        private long _fixedStepNanoseconds;

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

            foreach (SensorConfiguration sensor in configuration.Sensors)
            {
                if (BasiliskTime.ToNanoseconds(sensor.Definition.SamplePeriodSeconds) % fixedStepNanoseconds != 0)
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
            Expect(HasSensorsInOrder(response.ConfiguredSensorIds, configuration.Sensors), "ConfigureRun", "configured_sensor_ids");
            Expect(response.SpiceEarthFrame == "ITRF93", "ConfigureRun", "spice_earth_frame");
            Expect(!string.IsNullOrWhiteSpace(response.BasiliskVersion), "ConfigureRun", "basilisk_version");

            _configuration = configuration;
            _fixedStepNanoseconds = fixedStepNanoseconds;
            BasiliskVersion = response.BasiliskVersion;
            PacingOverrunCount = 0;
            IsInitialized = true;
        }

        public void Reset()
        {
            if (!IsInitialized)
            {
                throw new InvalidOperationException("BasiliskEngine has not been initialized.");
            }

            PbBasilisk.ResetRequest request = new PbBasilisk.ResetRequest { RunId = _configuration.RunId };
            PbBasilisk.ResetResponse response = Call("Reset", options => _client.Reset(request, options), ResetDeadline);
            Expect(response.RunId == _configuration.RunId, "Reset", "run_id");
            PacingOverrunCount = 0;
        }

        public bool TryStep(SimulationStepInput input, out SimulationSnapshot snapshot)
        {
            snapshot = default;
            if (!IsInitialized || !input.IsValid)
            {
                return false;
            }

            long simulationTimeNanoseconds = checked(input.Sequence * _fixedStepNanoseconds);
            if (Math.Abs(BasiliskTime.ToSeconds(simulationTimeNanoseconds) - input.SimulationTimeSeconds) > TimeToleranceSeconds)
            {
                throw new InvalidOperationException(
                    $"Step {input.Sequence} at {input.SimulationTimeSeconds:R} s is off the {_fixedStepNanoseconds} ns grid.");
            }

            PbBasilisk.StepRequest request = new PbBasilisk.StepRequest
            {
                RunId = _configuration.RunId,
                Sequence = (ulong)input.Sequence,
                SimTimeNs = (ulong)simulationTimeNanoseconds,
                Command = BasiliskProtoMapper.ToProto(input.Commands)
            };
            PbBasilisk.StepResponse response = Call("Step", options => _client.Step(request, options), StepDeadline());
            Expect(response.RunId == _configuration.RunId, "Step", "run_id");
            Expect(response.Sequence == request.Sequence, "Step", "sequence");
            Expect(response.SimTimeNs == request.SimTimeNs, "Step", "sim_time_ns");

            BasiliskStepState step = BasiliskProtoMapper.ToStepState(response);
            EnvironmentState environment = BasiliskStateMapper.MapEnvironment(step);
            SpacecraftState spacecraft = BasiliskStateMapper.MapSpacecraft(
                input.Sequence,
                _configuration.EpochUtc,
                step,
                environment);
            SensorMeasurementSet measurements = BasiliskStateMapper.MapMeasurements(step, _configuration.Sensors);
            snapshot = new SimulationSnapshot(
                _configuration.RunId,
                BackendName,
                spacecraft,
                input.Commands,
                environment: environment,
                sensorMeasurements: measurements);
            PacingOverrunCount = response.PacingOverrunCount;
            if (!snapshot.IsValid)
            {
                throw new InvalidOperationException($"Basilisk step {input.Sequence} mapped to an invalid snapshot.");
            }

            return true;
        }

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
            catch (RpcException exception)
            {
                throw new InvalidOperationException(
                    $"Basilisk {rpc} failed: {exception.StatusCode}: {exception.Status.Detail}",
                    exception);
            }
        }

        private static bool HasSensorsInOrder(IReadOnlyList<string> configuredIds, IReadOnlyList<SensorConfiguration> sensors)
        {
            if (configuredIds.Count != sensors.Count)
            {
                return false;
            }

            for (int i = 0; i < sensors.Count; i++)
            {
                if (!string.Equals(configuredIds[i], sensors[i].Definition.SensorId, StringComparison.Ordinal))
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
