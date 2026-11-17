using System;
using System.IO;
using System.Threading;
using Argus.Simulation.Core;

namespace Argus.Simulation.Host
{
    // Headless simulation host (G2): the gateway and sensor models over BasiliskEngine, recording
    // dataset v1 with Core RunRecorder when --record is given (D6), and streaming decimated states
    // and sensor frames to Unity through Streaming/StateStreamServer (CON-1).
    // TODO(D7, G1): HOST-6 serves the gateway's controller interface (IControllerGateway, CORE-9) to HIL and
    // external controllers through Gateway/GatewayServer.
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

            // A bad run file or spacecraft profile is bad input, rejected before the host connects
            // to the service.
            RunConfigurationFile.LoadedRun run;
            try
            {
                run = RunConfigurationFile.Read(
                    options.RunConfigurationPath,
                    "host-" + Guid.NewGuid().ToString("N"));
            }
            catch (RunConfigurationException exception)
            {
                Console.Error.WriteLine($"Run configuration {options.RunConfigurationPath}: {exception.Message}");
                return 2;
            }

            string physicalActuatorProblem = PhysicalActuatorProblem(run.Configuration, options.RealTimeFactor);
            if (physicalActuatorProblem != null)
            {
                Console.Error.WriteLine($"Run configuration {options.RunConfigurationPath}: {physicalActuatorProblem}");
                return 2;
            }

            // A recording that cannot work is also bad input, rejected before connecting: an ID the
            // recorder cannot turn into a directory name, or a root it cannot write.
            using RunRecorder recorder = options.RecordDirectory == null ? null : OpenRecorder(options, run);
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
                Run(options, run, recorder, cancellation.Token);
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

        // Why a run that lifts the physical-actuator inhibit can't run at this pace, or null (CORE-9).
        // HIL runs are paced by Basilisk (D2): an unpaced run would step hardware as fast as the host
        // can. The gateway handles a controller's calls between steps, so a healthy holder is heard
        // about once per paced step, and the failsafe acts at step starts: the timeout must cover two
        // steps, so a healthy holder is not lost by a step's own length (a pacing overrun can still
        // stretch one) and a silent one's last command runs for at most the timeout plus one step.
        internal static string PhysicalActuatorProblem(SimulationConfiguration configuration, double realTimeFactor)
        {
            if (!configuration.PhysicalActuators.Enabled)
            {
                return null;
            }

            if (realTimeFactor <= 0.0)
            {
                return "physicalActuators: needs a paced run (--real-time-factor > 0)";
            }

            double stepWallSeconds = configuration.FixedStepSeconds / realTimeFactor;
            if (stepWallSeconds > configuration.HeartbeatTimeoutWallSeconds / 2.0)
            {
                return "physicalActuators: a paced step lasts " +
                    $"{stepWallSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} s of wall time " +
                    "(fixedStepSeconds / --real-time-factor), more than half of heartbeatTimeoutWallSeconds " +
                    $"({configuration.HeartbeatTimeoutWallSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} s): " +
                    "the gateway hears a controller once per step";
            }

            return null;
        }

        private static RunRecorder OpenRecorder(HostOptions options, RunConfigurationFile.LoadedRun run)
        {
            SimulationConfiguration configuration = run.Configuration;
            for (int index = 0; index < configuration.Devices.Count; index++)
            {
                string deviceId = configuration.Devices[index].Definition.SensorId;
                if (!RunRecorder.IsPathSafeId(deviceId))
                {
                    // Named as the parser names a device: by its profile file, or inside the run file.
                    string device = run.ProfileFile == null
                        ? $"spacecraftProfile.devices[{index}]"
                        : $"spacecraft profile {run.ProfileFile}: devices[{index}]";
                    Console.Error.WriteLine(
                        $"Run configuration {options.RunConfigurationPath}: {device}.deviceId: '{deviceId}' cannot be " +
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

        private static void Run(HostOptions options, RunConfigurationFile.LoadedRun run, RunRecorder recorder, CancellationToken cancellation)
        {
            // Bound before the service is configured, so a busy port fails the run before it starts.
            StateStreamServer stream = StateStreamServer.StartAsync(options.StreamEndpoint, options.StreamPeriod)
                .GetAwaiter().GetResult();
            bool ranToEnd = false;
            try
            {
                Run(options, run, recorder, stream, cancellation);
                ranToEnd = true;
            }
            finally
            {
                try
                {
                    stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception) when (!ranToEnd)
                {
                    // The run's own failure is the one to report; this one is only logged.
                    Console.Error.WriteLine($"Stopping the state stream also failed: {exception.Message}");
                }
            }
        }

        private static void Run(
            HostOptions options,
            RunConfigurationFile.LoadedRun run,
            RunRecorder recorder,
            StateStreamServer stream,
            CancellationToken cancellation)
        {
            SimulationConfiguration configuration = run.Configuration;
            using BasiliskEngine engine = new BasiliskEngine(options.BasiliskAddress, options.RealTimeFactor, cancellation);
            SimulationGateway gateway = new SimulationGateway(engine);

            // Truth and every sensor frame come from the gateway's events; observations hold only
            // what a controller may see (D7).
            SimulationState state = default;
            long frameCount = 0;
            gateway.StateProduced += produced => state = produced;
            gateway.SensorOutputProduced += output => frameCount += output.Frames.Count;
            // The run still ends as it would have; the failure only goes to the log.
            gateway.SensorEndFailed += failure =>
                Console.Error.WriteLine($"A sensor could not release its run: {failure.Message}");

            // The stream only replaces its latest pair on this thread, so a slow client never delays
            // a step.
            gateway.RunStarted += (started, sensors) => stream.Publisher.BeginGeneration(started);
            gateway.StateProduced += stream.Publisher.PublishState;
            gateway.SensorOutputProduced += stream.Publisher.PublishSensorOutput;

            // The recorder only queues on this thread, so a slow disk never delays a step.
            if (recorder != null)
            {
                gateway.RunStarted += (started, sensors) => recorder.BeginRun(
                    started, sensors, engine.BasiliskVersion, spacecraftProfileId: run.ProfileId, spacecraftProfilePath: run.ProfilePath);
                gateway.StateProduced += recorder.RecordState;
                gateway.SensorOutputProduced += recorder.RecordSensorOutput;
                gateway.CommandLogged += recorder.RecordCommand;
                gateway.AuthorityLogged += recorder.RecordAuthority;
                gateway.RunTerminated += recorder.RecordTermination;
            }

            Observation observation = gateway.Reset(configuration.RandomSeed, configuration);
            Console.WriteLine(
                $"Basilisk {engine.BasiliskVersion}, kernel set {configuration.KernelSetId}, " +
                $"{configuration.Devices.Count} devices, run {configuration.RunId}, " +
                $"state stream on {stream.Endpoint} at {options.StreamRateHz:R} Hz " +
                $"(every {options.StreamPeriod.TotalMilliseconds:R} ms)");

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
                        // The run reached its duration: its sensors release what they hold (CORE-21).
                        gateway.Finish();
                        break;
                    }

                    cancellation.ThrowIfCancellationRequested();
                    // TODO(G1): no controller attaches until the gateway server (HOST-6) exists, so every step
                    // is idle. Once one can, publish observation k, give the holder a bounded window
                    // for command k (paced: part of the step's period; unpaced: a stated wait), then
                    // step: a command that arrives later is late and the step holds (CORE-9).
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
