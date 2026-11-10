using System;
using System.IO;
using System.Threading;
using Argus.Simulation.Core;

namespace Argus.Simulation.Host
{
    // Headless simulation host (G2): the gateway and sensor models over BasiliskEngine, recording
    // dataset v1 with Core RunRecorder when --record is given (D6), and streaming decimated states
    // and sensor frames to Unity through Streaming/StateStreamServer (CON-1).
    // TODO(D7, G1): serve the gateway to HIL and external controllers through Gateway/GatewayServer.
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (!HostOptions.TryParse(args, out HostOptions options, out string error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine(HostOptions.Usage);
                return 2;
            }

            // A bad run file is bad input, rejected before the host connects to the service.
            SimulationConfiguration configuration;
            try
            {
                configuration = RunConfigurationFile.Load(
                    options.RunConfigurationPath,
                    "host-" + Guid.NewGuid().ToString("N"));
            }
            catch (RunConfigurationException exception)
            {
                Console.Error.WriteLine($"Run configuration {options.RunConfigurationPath}: {exception.Message}");
                return 2;
            }

            // A recording that cannot work is also bad input, rejected before connecting: an ID the
            // recorder cannot turn into a directory name, or a root it cannot write.
            using RunRecorder recorder = options.RecordDirectory == null ? null : OpenRecorder(options, configuration);
            if (options.RecordDirectory != null && recorder == null)
            {
                return 2;
            }

            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            try
            {
                Run(options, configuration, recorder, cancellation.Token);
                return 0;
            }
            catch (Exception exception) when (cancellation.IsCancellationRequested)
            {
                // Cancellation reaches us wrapped (for example an RpcException inside an
                // InvalidOperationException), so the token, not the exception type, decides.
                Console.Error.WriteLine($"Cancelled: {exception.Message}");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        private static RunRecorder OpenRecorder(HostOptions options, SimulationConfiguration configuration)
        {
            for (int index = 0; index < configuration.Devices.Count; index++)
            {
                string deviceId = configuration.Devices[index].Definition.SensorId;
                if (!RunRecorder.IsPathSafeId(deviceId))
                {
                    Console.Error.WriteLine(
                        $"Run configuration {options.RunConfigurationPath}: devices[{index}].deviceId: '{deviceId}' cannot be " +
                        "recorded; --record needs [a-z0-9][a-z0-9._-]*, not ending in '.' (Core/Recording/README.md)");
                    return null;
                }
            }

            try
            {
                return new RunRecorder(options.RecordDirectory, "headless-host", RunRecorder.CommitOf(typeof(Program).Assembly));
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"--record: {exception.Message}");
                return null;
            }
        }

        private static void Run(HostOptions options, SimulationConfiguration configuration, RunRecorder recorder, CancellationToken cancellation)
        {
            // Bound before the service is configured, so a busy port fails the run before it starts.
            StateStreamServer stream = StateStreamServer.StartAsync(options.StreamEndpoint, options.StreamRateHz)
                .GetAwaiter().GetResult();
            try
            {
                Run(options, configuration, recorder, stream, cancellation);
            }
            finally
            {
                stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        private static void Run(
            HostOptions options,
            SimulationConfiguration configuration,
            RunRecorder recorder,
            StateStreamServer stream,
            CancellationToken cancellation)
        {
            using BasiliskEngine engine = new BasiliskEngine(options.BasiliskAddress, options.RealTimeFactor, cancellation);
            SimulationGateway gateway = new SimulationGateway(engine);

            // Truth and every sensor frame come from the gateway's events; observations hold only
            // what a controller may see (D7).
            SimulationState state = default;
            long frameCount = 0;
            gateway.StateProduced += produced => state = produced;
            gateway.SensorOutputProduced += output => frameCount += output.Frames.Count;

            // The stream only replaces its latest pair on this thread, so a slow client never delays
            // a step.
            gateway.RunStarted += (run, sensors) => stream.Publisher.BeginGeneration(run);
            gateway.StateProduced += stream.Publisher.PublishState;
            gateway.SensorOutputProduced += stream.Publisher.PublishSensorOutput;

            // The recorder only queues on this thread, so a slow disk never delays a step.
            if (recorder != null)
            {
                gateway.RunStarted += (run, sensors) => recorder.BeginRun(run, sensors, engine.BasiliskVersion);
                gateway.StateProduced += recorder.RecordState;
                gateway.SensorOutputProduced += recorder.RecordSensorOutput;
                gateway.CommandLogged += recorder.RecordCommand;
                gateway.RunTerminated += recorder.RecordTermination;
            }

            Observation observation = gateway.Reset(configuration.RandomSeed, configuration);
            Console.WriteLine(
                $"Basilisk {engine.BasiliskVersion}, kernel set {configuration.KernelSetId}, " +
                $"{configuration.Devices.Count} devices, run {configuration.RunId}, " +
                $"state stream on {stream.Endpoint} at {options.StreamRateHz:R} Hz");

            long lastSequence = (long)Math.Round(options.DurationSeconds / configuration.FixedStepSeconds);
            long stepsPerReport = Math.Max(1L, (long)Math.Round(1.0 / configuration.FixedStepSeconds));
            try
            {
                while (true)
                {
                    if (observation.IsTerminal)
                    {
                        throw new InvalidOperationException(observation.TerminationReason, gateway.Failure);
                    }

                    // A recorder that stopped writing ends the run now, not after it.
                    if (recorder?.Failure != null)
                    {
                        gateway.Stop($"The run recorder failed: {recorder.Failure.Message}");
                        throw new InvalidOperationException($"The run recorder failed: {recorder.Failure.Message}", recorder.Failure);
                    }

                    if (observation.Sequence % stepsPerReport == 0)
                    {
                        SpacecraftState spacecraft = state.Spacecraft;
                        Console.WriteLine(
                            $"t={spacecraft.SimulationTimeSeconds,8:F1} s  seq={spacecraft.Sequence,6}  " +
                            $"|r|={spacecraft.PositionEcefMeters.Magnitude / 1000.0,9:F3} km  " +
                            $"shadow={state.Environment.Value.SpacecraftShadowFactor:F3}  " +
                            $"frames={frameCount}  overruns={engine.PacingOverrunCount}");
                    }

                    if (observation.Sequence >= lastSequence)
                    {
                        break;
                    }

                    cancellation.ThrowIfCancellationRequested();
                    // TODO(G1): no controller is attached yet, so every command is zero.
                    observation = gateway.Step();
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // The manifest says the run was stopped, not that it reached its duration. A Ctrl+C
                // during a Step RPC has already ended it, with the cancelled step as the reason.
                gateway.Stop("Cancelled (Ctrl+C)");
                throw;
            }

            if (recorder != null)
            {
                recorder.Close();
                Console.WriteLine($"Recorded run {configuration.RunId} under {Path.GetFullPath(options.RecordDirectory)}");
            }
        }
    }
}
