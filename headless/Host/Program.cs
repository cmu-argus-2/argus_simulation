using System;
using System.Threading;
using Argus.Simulation.Core;

namespace Argus.Simulation.Host
{
    // Headless simulation host (G2): the gateway and sensor models over BasiliskEngine.
    // TODO(D6): feed every SimulationState and SensorOutputSet to Core RunRecorder.
    // TODO(D7, G1): serve the gateway to HIL and external controllers through Gateway/GatewayServer.
    // TODO(G2): stream decimated states to Unity through Streaming/StateStreamServer.
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

            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            try
            {
                Run(options, configuration, cancellation.Token);
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

        private static void Run(HostOptions options, SimulationConfiguration configuration, CancellationToken cancellation)
        {
            using BasiliskEngine engine = new BasiliskEngine(options.BasiliskAddress, options.RealTimeFactor, cancellation);
            SimulationGateway gateway = new SimulationGateway(engine);

            // Truth and every sensor frame come from the gateway's events; observations hold only
            // what a controller may see (D7).
            SimulationState state = default;
            long frameCount = 0;
            gateway.StateProduced += produced => state = produced;
            gateway.SensorOutputProduced += output => frameCount += output.Frames.Count;

            Observation observation = gateway.Reset(configuration.RandomSeed, configuration);
            Console.WriteLine(
                $"Basilisk {engine.BasiliskVersion}, kernel set {configuration.KernelSetId}, " +
                $"{configuration.Sensors.Count} sensors, run {configuration.RunId}");

            long lastSequence = (long)Math.Round(options.DurationSeconds / configuration.FixedStepSeconds);
            long stepsPerReport = Math.Max(1L, (long)Math.Round(1.0 / configuration.FixedStepSeconds));
            while (true)
            {
                if (observation.IsTerminal)
                {
                    throw new InvalidOperationException(observation.TerminationReason);
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
                    return;
                }

                cancellation.ThrowIfCancellationRequested();
                // TODO(G1): no controller is attached yet, so every command is zero.
                observation = gateway.Step();
            }
        }
    }
}
