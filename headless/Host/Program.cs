using System;
using System.Threading;
using Argus.Simulation.Core;

namespace Argus.Simulation.Host
{
    // Headless simulation host (G2): the gateway and sensor models over BasiliskEngine. The recorder
    // (D6), the gateway server for agents and HIL (D7, G1) and the Unity snapshot stream (G2) attach
    // here later.
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

            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            try
            {
                Run(options, cancellation.Token);
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

        private static void Run(HostOptions options, CancellationToken cancellation)
        {
            SimulationConfiguration configuration = HostScenario.CreateP0Default("host-" + Guid.NewGuid().ToString("N"));
            using BasiliskEngine engine = new BasiliskEngine(options.BasiliskAddress, options.RealTimeFactor, cancellation);
            SimulationGateway gateway = new SimulationGateway(engine);
            gateway.Initialize(configuration);
            Console.WriteLine(
                $"Basilisk {engine.BasiliskVersion}, kernel set {configuration.KernelSetId}, " +
                $"{configuration.Sensors.Count} sensors, run {configuration.RunId}");

            SensorManager sensors = new SensorManager();
            foreach (SensorConfiguration sensor in configuration.Sensors)
            {
                sensors.Register(SensorFactory.Create(sensor));
            }

            sensors.Reset(new SensorResetContext(configuration.RunId, configuration.EpochUtc, configuration.RandomSeed));

            long lastSequence = (long)Math.Round(options.DurationSeconds / configuration.FixedStepSeconds);
            long stepsPerReport = Math.Max(1L, (long)Math.Round(1.0 / configuration.FixedStepSeconds));
            long frameCount = 0;
            for (long sequence = 0; sequence <= lastSequence; sequence++)
            {
                cancellation.ThrowIfCancellationRequested();
                SimulationSnapshot snapshot = gateway.Step();
                SensorOutputSet frames = sensors.Sample(new SensorSampleContext(configuration.RunId, snapshot));
                frameCount += frames.Frames.Count;

                if (sequence % stepsPerReport == 0)
                {
                    SpacecraftState state = snapshot.Spacecraft;
                    Console.WriteLine(
                        $"t={state.SimulationTimeSeconds,8:F1} s  seq={state.Sequence,6}  " +
                        $"|r|={state.PositionEcefMeters.Magnitude / 1000.0,9:F3} km  " +
                        $"shadow={snapshot.Environment.Value.SpacecraftShadowFactor:F3}  " +
                        $"frames={frameCount}  overruns={engine.PacingOverrunCount}");
                }
            }
        }
    }
}
