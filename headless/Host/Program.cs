using System;
using System.IO;
using System.Threading;
using Argus.Simulation.Core;

namespace Argus.Simulation.Host
{
    // Headless simulation host (G2): the gateway and sensor models over BasiliskEngine, recording
    // dataset v1 with Core RunRecorder when --record is given (D6), streaming decimated states and
    // sensor frames to Unity through Streaming/StateStreamServer (CON-1), and serving the gateway's
    // controller interface (IControllerGateway, CORE-9) to HIL adapters and external controllers
    // through Gateway/GatewayServer (CON-3, HOST-6).
    public static class Program
    {
        // How long a lockstep run waits for a controller to claim authority after the last one lost it
        // (CORE-9), and how often its loop checks the run between controller calls.
        internal static readonly TimeSpan LockstepClaimTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan LockstepPollInterval = TimeSpan.FromMilliseconds(100);

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

        // Why the host refuses controller sessions for this run, or null (HOST-6). In a run the host
        // steps, a controller that hangs keeps authority until the heartbeat timeout: with none, its
        // last command would be held for the rest of the run, and a timeout shorter than two steps
        // (a paced step, or the command window) would drop a healthy controller. A lockstep run stops
        // while its controller is silent, so it may run without a timeout (CORE-9).
        internal static string SessionRefusal(SimulationConfiguration configuration, HostOptions options)
        {
            if (options.StepDriver != StepDriver.Host)
            {
                return null;
            }

            double timeout = configuration.HeartbeatTimeoutWallSeconds;
            if (timeout <= 0.0)
            {
                return "This run has no heartbeat timeout (heartbeatTimeoutWallSeconds), so a controller that hangs " +
                    "would hold its last command for the rest of the run; the host serves no controller sessions.";
            }

            double stepWallSeconds = options.RealTimeFactor > 0.0
                ? configuration.FixedStepSeconds / options.RealTimeFactor
                : options.CommandWindow.TotalSeconds;
            if (stepWallSeconds > timeout / 2.0)
            {
                return $"A step can last {stepWallSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} s " +
                    "of wall time, more than half of heartbeatTimeoutWallSeconds " +
                    $"({timeout.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} s), so a healthy " +
                    "controller could be lost; the host serves no controller sessions.";
            }

            return null;
        }

        // How long an owner-stepped step waits for the holder's command: --command-window-ms, and at
        // most half a paced step, so the wait fits in the step's period.
        internal static TimeSpan CommandWindow(SimulationConfiguration configuration, HostOptions options)
        {
            if (options.RealTimeFactor <= 0.0)
            {
                return options.CommandWindow;
            }

            TimeSpan halfStep = TimeSpan.FromSeconds(configuration.FixedStepSeconds / options.RealTimeFactor / 2.0);
            return options.CommandWindow < halfStep ? options.CommandWindow : halfStep;
        }

        // A lockstep run (StepDriver.Controller): the controller holding authority steps it and may
        // reset it, through calls this loop runs between its checks. It ends when the current run
        // reaches the duration (a controller's Reset starts the count again), fails, is stopped, or
        // stays without an authority holder for LockstepClaimTimeout after losing one; a run that
        // ended has no holder to reset it, so the host exits then, as an owner-stepped run does.
        private static void RunLockstep(
            SimulationGateway gateway,
            GatewayHub hub,
            RunRecorder recorder,
            long lastSequence,
            Func<Observation> terminal,
            CancellationToken cancellation)
        {
            bool hadHolder = false;
            TimeSpan? lostAt = null;
            while (true)
            {
                Observation ended = terminal();
                if (ended != null)
                {
                    throw new InvalidOperationException(ended.TerminationReason, gateway.Failure);
                }

                if (recorder?.Failure != null)
                {
                    gateway.Stop($"The run recorder failed: {recorder.Failure.Message}");
                    throw new InvalidOperationException($"The run recorder failed: {recorder.Failure.Message}", recorder.Failure);
                }

                if (gateway.NextSequence >= lastSequence)
                {
                    // The run reached its duration: its sensors release what they hold (CORE-21).
                    gateway.Finish();
                    return;
                }

                // A holder that went quiet loses authority here, as a missed heartbeat does in a run
                // the host steps; a lockstep run without a holder only waits, for a bounded time.
                gateway.CheckHeartbeat();
                if (gateway.AuthorityHolder != null)
                {
                    hadHolder = true;
                    lostAt = null;
                }
                else if (hadHolder)
                {
                    lostAt ??= hub.Dispatcher.Now;
                    if (hub.Dispatcher.Now - lostAt.Value >= LockstepClaimTimeout)
                    {
                        string reason = $"No controller claimed authority within {LockstepClaimTimeout.TotalSeconds:R} s of the last one losing it.";
                        gateway.Stop(reason);
                        throw new InvalidOperationException(reason);
                    }
                }

                cancellation.ThrowIfCancellationRequested();
                hub.Dispatcher.Pump(LockstepPollInterval, () => false);
            }
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
            // Both bound before the service is configured, so a busy port fails the run before it starts.
            StateStreamServer stream = StateStreamServer.StartAsync(options.StreamEndpoint, options.StreamPeriod)
                .GetAwaiter().GetResult();
            GatewayDispatcher dispatcher = new GatewayDispatcher();
            SimulationConfiguration configuration = run.Configuration;
            GatewayHub hub = new GatewayHub(dispatcher, configuration, options.StepDriver, SessionRefusal(configuration, options));
            GatewayServer gatewayServer = null;
            bool ranToEnd = false;
            try
            {
                gatewayServer = GatewayServer.StartAsync(options.GatewayEndpoint, hub).GetAwaiter().GetResult();
                Run(options, run, recorder, stream, gatewayServer, cancellation);
                ranToEnd = true;
            }
            finally
            {
                // No link call runs after the loop: calls still waiting are refused, then every
                // session ends.
                dispatcher.Complete();
                Stop("controller gateway", gatewayServer, ranToEnd);
                Stop("state stream", stream, ranToEnd);
            }
        }

        private static void Stop(string what, IAsyncDisposable server, bool ranToEnd)
        {
            try
            {
                server?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception) when (!ranToEnd)
            {
                // The run's own failure is the one to report; this one is only logged.
                Console.Error.WriteLine($"Stopping the {what} also failed: {exception.Message}");
            }
        }

        private static void Run(
            HostOptions options,
            RunConfigurationFile.LoadedRun run,
            RunRecorder recorder,
            StateStreamServer stream,
            GatewayServer gatewayServer,
            CancellationToken cancellation)
        {
            SimulationConfiguration configuration = run.Configuration;
            using BasiliskEngine engine = new BasiliskEngine(options.BasiliskAddress, options.RealTimeFactor, cancellation);
            GatewayHub hub = gatewayServer.Hub;
            // A link call's heartbeat is timed by when it arrived, not when the loop ran it (CORE-9).
            SimulationGateway gateway = new SimulationGateway(
                engine, stepDriver: options.StepDriver, clock: hub.Dispatcher.GatewayClock);

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

            // Controllers see each observation and run end; their calls run on this thread (HOST-6).
            hub.Attach(gateway);
            Observation terminal = null;
            gateway.RunStarted += (run, sensors) => terminal = null;
            gateway.RunTerminated += ended => terminal = ended;

            Observation observation = gateway.Reset(configuration.RandomSeed, configuration);
            Console.WriteLine(
                $"Basilisk {engine.BasiliskVersion}, kernel set {configuration.KernelSetId}, " +
                $"{configuration.Devices.Count} devices, run {configuration.RunId}, " +
                $"state stream on {stream.Endpoint} at {options.StreamRateHz:R} Hz " +
                $"(every {options.StreamPeriod.TotalMilliseconds:R} ms), " +
                $"controller gateway on {gatewayServer.Endpoint} " +
                (options.StepDriver == StepDriver.Controller ? "(lockstep: the authority holder steps)" : "(the host steps)") +
                (hub.SessionRefusal == null ? "" : $", refusing sessions: {hub.SessionRefusal}"));

            long lastSequence = (long)Math.Round(options.DurationSeconds / configuration.FixedStepSeconds);
            long stepsPerReport = Math.Max(1L, (long)Math.Round(1.0 / configuration.FixedStepSeconds));
            TimeSpan commandWindow = CommandWindow(configuration, options);
            try
            {
                // One progress line per simulated second, from the latest truth.
                void Report()
                {
                    SpacecraftState spacecraft = state.Spacecraft;
                    Console.WriteLine(
                        $"t={spacecraft.SimulationTimeSeconds,8:F1} s  seq={spacecraft.Sequence,6}  " +
                        $"|r|={spacecraft.PositionEcefMeters.Magnitude / 1000.0,9:F3} km  " +
                        $"shadow={state.Environment.Value.SpacecraftShadowFactor:F3}  " +
                        $"frames={frameCount}  overruns={engine.PacingOverrunCount}");
                }

                if (options.StepDriver == StepDriver.Controller)
                {
                    // The controller steps, so each simulated second is reported as its observation
                    // is published.
                    gateway.ObservationPublished += published =>
                    {
                        if (!published.IsTerminal && published.Sequence % stepsPerReport == 0)
                        {
                            Report();
                        }
                    };
                    RunLockstep(gateway, hub, recorder, lastSequence, () => terminal, cancellation);
                }
                else
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
                            Report();
                        }

                        if (observation.Sequence >= lastSequence)
                        {
                            // The run reached its duration: its sensors release what they hold (CORE-21).
                            gateway.Finish();
                            break;
                        }

                        cancellation.ThrowIfCancellationRequested();
                        // Observation k is out; the holder has a bounded window for command k, and a
                        // command that arrives later is late and the step holds (CORE-9). With no holder
                        // the loop only runs the calls already waiting.
                        hub.Dispatcher.Pump(
                            gateway.AuthorityHolder == null ? TimeSpan.Zero : commandWindow,
                            () => gateway.AuthorityHolder == null || hub.SubmittedSequence == gateway.NextSequence);
                        gateway.CheckHeartbeat();
                        observation = gateway.Step();
                    }
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
