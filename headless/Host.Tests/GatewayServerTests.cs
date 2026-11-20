using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Argus.Simulation.Core;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using PbGateway = Argus.Contracts.Gateway.V1;
using PbSim = Argus.Contracts.Sim.V1;

namespace Argus.Simulation.Host.Tests
{
    // The controller gateway (HOST-6) over argus/gateway/v1 (CON-3): sessions, authority, lockstep
    // steps and resets, commands in a run the host steps, and the session rules, served in process
    // over the analytic engine.
    internal sealed class GatewayServerTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
        private const string TestProfileId = "test-profile";

        // Acceptance (#30): a client resets and steps a lockstep run, then gets identical
        // observations after a second Reset with the same seed.
        [Test]
        public async Task ALockstepControllerStepsAndAResetRepeatsTheRun()
        {
            await using Served served = await Served.StartAsync(StepDriver.Controller, heartbeatTimeoutSeconds: 0.0);
            await using Controller controller = await Controller.ConnectAsync(served);
            Assert.That(controller.Session.StepDriver, Is.EqualTo(PbGateway.StepDriver.Controller));
            Assert.That(controller.Session.MaxMessageBytes, Is.EqualTo((ulong)GatewayMapper.DefaultMaxMessageBytes));
            Assert.That(controller.Session.HasHeartbeatTimeoutWallS, Is.True);
            Assert.That(controller.Session.HasCommandWindowWallS, Is.False, "a lockstep run has no command window");
            Assert.That(controller.Session.PhysicalActuators, Is.Not.Null, "always sent");
            Assert.That(controller.Session.PhysicalActuators.Enabled, Is.False);
            Assert.That(controller.Session.SpacecraftProfileId, Is.EqualTo(TestProfileId));
            PbGateway.Observation zero = (await controller.NextAsync()).Observation;
            Assert.That((zero.Sequence, zero.Status), Is.EqualTo((0L, PbGateway.ObservationStatus.Ok)), "the running run's latest observation");

            await controller.ExpectAcceptedAsync(Claim());
            List<PbGateway.Observation> first = await StepAsync(controller, zero, 20);

            await controller.SendAsync(Reset(0), 99);
            Assert.That((await controller.NextAsync()).MessageCase, Is.EqualTo(PbGateway.ConnectResponse.MessageOneofCase.RunEnded));
            PbGateway.Observation again = (await controller.NextAsync()).Observation;
            Assert.That(again.Sequence, Is.EqualTo(0));
            PbGateway.CallResult reset = (await controller.NextAsync()).Result;
            Assert.That((reset.RequestId, reset.Accepted), Is.EqualTo((99UL, true)));

            // A Reset clears authority (CORE-9).
            await controller.ExpectRefusedAsync(Step(again));
            await controller.ExpectAcceptedAsync(Claim());
            List<PbGateway.Observation> second = await StepAsync(controller, again, 20);
            Assert.That(second.Select(o => o.ToByteString()), Is.EqualTo(first.Select(o => o.ToByteString())));
        }

        // Every session gets every observation, but only the holder steps; the other's refused step
        // decoded, so it is logged (CORE-7).
        [Test]
        public async Task AnotherSessionSeesEveryObservationButCannotStep()
        {
            await using Served served = await Served.StartAsync(StepDriver.Controller, heartbeatTimeoutSeconds: 0.0);
            await using Controller holder = await Controller.ConnectAsync(served);
            await using Controller watcher = await Controller.ConnectAsync(served);
            Assert.That(watcher.Session.ControllerId, Is.Not.EqualTo(holder.Session.ControllerId));
            PbGateway.Observation zero = (await holder.NextAsync()).Observation;
            Assert.That((await watcher.NextAsync()).Observation.Sequence, Is.EqualTo(0));

            await holder.ExpectAcceptedAsync(Claim());
            await StepAsync(holder, zero, 1);
            Assert.That((await watcher.NextAsync()).Observation.Sequence, Is.EqualTo(1));

            int logged = served.Commands.Count;
            PbGateway.CallResult refused = await watcher.ExpectRefusedAsync(Step(new PbGateway.Observation { Sequence = 1, SimulationTimeS = 0.1 }));
            Assert.That(refused.Refusal, Does.Contain(holder.Session.ControllerId).Or.Contain("authority"));
            Assert.That(served.Commands.Count, Is.EqualTo(logged + 1));
            Assert.That(served.Commands.Last().ControllerId, Is.EqualTo(watcher.Session.ControllerId));
        }

        // A command that doesn't decode never became a command: refused by field, not logged.
        [TestCase("sequence", "step.command.sequence: required field is not set")]
        [TestCase("time", "step.command.apply_at_simulation_time_s: required field is not set")]
        [TestCase("commands", "step.command.commands: required field is not set")]
        [TestCase("value", "step.command.commands.commands[0].values[\"torque_body_n_m\"]: no value is set")]
        public async Task AnUndecodableCommandIsRefusedByFieldAndNotLogged(string broken, string expected)
        {
            await using Served served = await Served.StartAsync(StepDriver.Controller, heartbeatTimeoutSeconds: 0.0);
            await using Controller controller = await Controller.ConnectAsync(served);
            PbGateway.Observation zero = (await controller.NextAsync()).Observation;
            await controller.ExpectAcceptedAsync(Claim());

            PbGateway.ConnectRequest request = Step(zero);
            PbGateway.CommandForStep command = request.Step.Command;
            switch (broken)
            {
                case "sequence":
                    command.ClearSequence();
                    break;
                case "time":
                    command.ClearApplyAtSimulationTimeS();
                    break;
                case "commands":
                    command.Commands = null;
                    break;
                default:
                    PbSim.DeviceCommand device = new PbSim.DeviceCommand { DeviceId = "torque.body" };
                    device.Values.Add("torque_body_n_m", new PbSim.DeviceValue());
                    command.Commands.Commands.Add(device);
                    break;
            }

            int logged = served.Commands.Count;
            Assert.That((await controller.ExpectRefusedAsync(request)).Refusal, Is.EqualTo(expected));
            Assert.That(served.Commands.Count, Is.EqualTo(logged));

            // A late command decodes, so the gateway refuses it and logs it.
            PbGateway.ConnectRequest late = Step(zero);
            late.Step.Command.Sequence = 5;
            await controller.ExpectRefusedAsync(late);
            Assert.That(served.Commands.Count, Is.EqualTo(logged + 1));
            Assert.That(served.Commands.Last().Outcome, Is.EqualTo(CommandOutcome.Rejected));
        }

        // Only the holder of a running lockstep run resets, and only with a seed.
        [Test]
        public async Task OnlyTheHolderResetsALockstepRun()
        {
            await using Served served = await Served.StartAsync(StepDriver.Controller, heartbeatTimeoutSeconds: 0.0);
            await using Controller controller = await Controller.ConnectAsync(served);
            await controller.NextAsync();
            Assert.That((await controller.ExpectRefusedAsync(Reset(1))).Refusal, Does.Contain("holding authority"));
            await controller.ExpectAcceptedAsync(Claim());
            Assert.That(
                (await controller.ExpectRefusedAsync(new PbGateway.ConnectRequest { Reset = new PbGateway.ResetRun() })).Refusal,
                Is.EqualTo("reset.seed: required field is not set"));
            Assert.That(
                (await controller.ExpectRefusedAsync(new PbGateway.ConnectRequest())).Refusal,
                Is.EqualTo("call: required field is not set"));
        }

        // A session that ends loses its authority (failsafe).
        [Test]
        public async Task EndingASessionRevokesItsAuthority()
        {
            await using Served served = await Served.StartAsync(StepDriver.Controller, heartbeatTimeoutSeconds: 0.0);
            Controller controller = await Controller.ConnectAsync(served);
            await controller.NextAsync();
            await controller.ExpectAcceptedAsync(Claim());
            Assert.That(await served.OnLoop(g => g.AuthorityHolder), Is.EqualTo(controller.Session.ControllerId));

            await controller.DisposeAsync();
            string holder = controller.Session.ControllerId;
            for (int attempt = 0; attempt < 200 && holder != null; attempt++)
            {
                await Task.Delay(25);
                holder = await served.OnLoop(g => g.AuthorityHolder);
            }

            Assert.That(holder, Is.Null);
        }

        // A host that stops right after its run ends still sends the run's end, then ends the session
        // with OK.
        [Test]
        public async Task AStoppingHostSendsWhatItQueuedThenEndsTheSession()
        {
            Served served = await Served.StartAsync(StepDriver.Controller, heartbeatTimeoutSeconds: 0.0);
            await using Controller controller = await Controller.ConnectAsync(served);
            await controller.NextAsync();
            await served.OnLoop(g =>
            {
                g.Finish();
                return true;
            });
            // Read while the host stops, as a controller does: the web host resets a connection
            // the client keeps open once its stop timeout passes, dropping what wasn't read yet.
            Task<(PbGateway.ConnectResponse.MessageOneofCase, bool)> reading = Task.Run(async () =>
            {
                PbGateway.ConnectResponse last = await controller.NextAsync();
                return (last.MessageCase, await controller.EndedAsync());
            });
            await served.DisposeAsync();

            (PbGateway.ConnectResponse.MessageOneofCase last, bool ended) = await reading;
            Assert.That(last, Is.EqualTo(PbGateway.ConnectResponse.MessageOneofCase.RunEnded));
            Assert.That(ended, Is.True);
        }

        // In a run the host steps, the holder submits command k and the host's step applies it; a
        // step with none holds the last one.
        [Test]
        public async Task TheHostAppliesTheHoldersSubmittedCommandAndHoldsWithoutOne()
        {
            await using Served served = await Served.StartAsync(StepDriver.Host, heartbeatTimeoutSeconds: 5.0, pumpOnItsOwn: false);
            await using Controller controller = await Controller.ConnectAsync(served, host: served);
            PbGateway.Observation zero = (await controller.NextAsync()).Observation;
            Assert.That(controller.Session.StepDriver, Is.EqualTo(PbGateway.StepDriver.Host));
            Assert.That(controller.Session.CommandWindowWallS, Is.EqualTo(0.05), "--command-window-ms's default");

            await served.HostCallAsync(controller, Claim());
            PbGateway.ConnectRequest submit = new PbGateway.ConnectRequest
            {
                Submit = new PbGateway.SubmitCommand { Command = Step(zero).Step.Command }
            };
            await served.HostCallAsync(controller, submit);
            Assert.That(served.Hub.SubmittedSequence, Is.EqualTo(0));

            // The host loop: step as soon as command k is in.
            served.HostStep(TimeSpan.FromSeconds(1));
            served.HostStep(TimeSpan.Zero);
            Assert.That(served.Commands.Select(c => c.Outcome), Is.EqualTo(new[] { CommandOutcome.Applied, CommandOutcome.Held }));
            Assert.That(served.Commands[0].ControllerId, Is.EqualTo(controller.Session.ControllerId));
            Assert.That((await controller.NextAsync()).Observation.Sequence, Is.EqualTo(1));
            Assert.That((await controller.NextAsync()).Observation.Sequence, Is.EqualTo(2));

            // A lockstep step is refused in a run the host steps, and logged.
            PbGateway.CallResult refused = await served.HostCallAsync(controller, Step(new PbGateway.Observation { Sequence = 2, SimulationTimeS = 0.2 }), accepted: false);
            Assert.That(refused.Refusal, Does.Contain("stepped by its owner"));
        }

        // A run the host steps without a heartbeat timeout serves no controller.
        [Test]
        public async Task TheHostRefusesSessionsWhenItsRunHasNoHeartbeatTimeout()
        {
            await using Served served = await Served.StartAsync(StepDriver.Host, heartbeatTimeoutSeconds: 0.0, pumpOnItsOwn: false);
            using GrpcChannel channel = served.Channel();
            using AsyncDuplexStreamingCall<PbGateway.ConnectRequest, PbGateway.ConnectResponse> call = Controller.Call(channel);
            RpcException refused = Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext(CancellationToken.None));
            Assert.That(refused.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition));
            Assert.That(refused.Status.Detail, Does.Contain("heartbeat timeout"));
        }

        [Test]
        public void SessionRefusalNeedsATimeoutCoveringTwoStepsOnlyWhenTheHostSteps()
        {
            SimulationConfiguration none = Configuration(0.0);
            SimulationConfiguration fiveSeconds = Configuration(5.0);
            Assert.That(Program.SessionRefusal(none, Options("--step-driver", "controller")), Is.Null);
            Assert.That(Program.SessionRefusal(none, Options()), Does.Contain("no heartbeat timeout"));
            Assert.That(Program.SessionRefusal(fiveSeconds, Options()), Is.Null);
            // A paced 0.1 s step at 1/40 real time lasts 4 s, more than half of 5 s.
            Assert.That(Program.SessionRefusal(fiveSeconds, Options("--real-time-factor", "0.025")), Does.Contain("more than half"));
            Assert.That(Program.SessionRefusal(fiveSeconds, Options("--command-window-ms", "3000")), Does.Contain("more than half"));
        }

        [Test]
        public void APacedCommandWindowIsAtMostHalfAStep()
        {
            SimulationConfiguration run = Configuration(5.0);
            Assert.That(Program.CommandWindow(run, Options()), Is.EqualTo(TimeSpan.FromMilliseconds(50)));
            Assert.That(Program.CommandWindow(run, Options("--real-time-factor", "1", "--command-window-ms", "80")), Is.EqualTo(TimeSpan.FromMilliseconds(50)));
            Assert.That(Program.CommandWindow(run, Options("--real-time-factor", "1", "--command-window-ms", "20")), Is.EqualTo(TimeSpan.FromMilliseconds(20)));
        }

        [Test]
        public void GatewayOptionsComeFromTheFlagThenTheEnvironmentThenTheDefault()
        {
            string saved = Environment.GetEnvironmentVariable(HostOptions.GatewayEndpointVariable);
            try
            {
                Environment.SetEnvironmentVariable(HostOptions.GatewayEndpointVariable, null);
                HostOptions defaults = Options();
                Assert.That(defaults.GatewayEndpoint, Is.EqualTo(new IPEndPoint(IPAddress.Loopback, 50053)));
                Assert.That((defaults.StepDriver, defaults.CommandWindow), Is.EqualTo((StepDriver.Host, TimeSpan.FromMilliseconds(50))));

                Environment.SetEnvironmentVariable(HostOptions.GatewayEndpointVariable, "127.0.0.1:6000");
                Assert.That(Options().GatewayEndpoint.Port, Is.EqualTo(6000));
                Assert.That(Options("--gateway-endpoint", "[::1]:6001").GatewayEndpoint, Is.EqualTo(new IPEndPoint(IPAddress.IPv6Loopback, 6001)));
            }
            finally
            {
                Environment.SetEnvironmentVariable(HostOptions.GatewayEndpointVariable, saved);
            }

            Assert.That(HostOptions.TryParse(new[] { "--gateway-endpoint", "10.0.0.1:50053" }, out _, out string remote), Is.False);
            Assert.That(remote, Does.Contain("loopback"));
            Assert.That(HostOptions.TryParse(new[] { "--step-driver", "unity" }, out _, out string driver), Is.False);
            Assert.That(driver, Does.Contain("host or controller"));
            Assert.That(HostOptions.TryParse(new[] { "--command-window-ms", "-1" }, out _, out _), Is.False);
            Assert.That(HostOptions.TryParse(new[] { "--command-window-ms", "10001" }, out _, out _), Is.False);
            Assert.That(Options("--step-driver", "controller").StepDriver, Is.EqualTo(StepDriver.Controller));
        }

        // max_message_bytes is the sum of every controller-visible camera's RGB8 image plus 1 MiB,
        // and never below gRPC's default.
        [Test]
        public void MaxMessageBytesSumsEveryControllerVisibleCamera()
        {
            SimulationConfiguration p0 = RunConfigurationFile.Load(HostOptions.DefaultRunConfigurationPath, "p0");
            Assert.That(GatewayMapper.MaxMessageBytes(p0), Is.EqualTo(4L * 1024 * 1024), "P0's cameras aren't controller-visible");

            // The devices live in the spacecraft profile the run file names (HOST-9): copy both,
            // keeping the run file's relative path to the profile, with every camera visible.
            string runFile = HostOptions.DefaultRunConfigurationPath;
            JsonObject run = JsonNode.Parse(File.ReadAllText(runFile)).AsObject();
            string profilePath = (string)run["spacecraftProfile"];
            string profileFile = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(runFile), profilePath));
            JsonObject profile = JsonNode.Parse(File.ReadAllText(profileFile)).AsObject();
            foreach (JsonNode device in profile["devices"].AsArray())
            {
                if ((string)device["kind"] == CameraKind.KindName)
                {
                    device["controllerVisible"] = true;
                }
            }

            string root = Path.Combine(Path.GetTempPath(), $"gateway-cameras-{Guid.NewGuid():N}");
            string copiedRun = Path.Combine(root, "runs", "run.json");
            string copiedProfile = Path.GetFullPath(Path.Combine(root, "runs", profilePath));
            Directory.CreateDirectory(Path.GetDirectoryName(copiedRun));
            Directory.CreateDirectory(Path.GetDirectoryName(copiedProfile));
            File.WriteAllText(copiedRun, run.ToJsonString());
            File.WriteAllText(copiedProfile, profile.ToJsonString());
            try
            {
                SimulationConfiguration visible = RunConfigurationFile.Load(copiedRun, "visible");
                Assert.That(GatewayMapper.MaxMessageBytes(visible), Is.EqualTo(4L * 4608 * 2592 * 3 + 1024 * 1024));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // A camera frame carries its CameraObservation and no payload; a terminal observation carries
        // its status but not its reason (D7).
        [Test]
        public void ObservationsCarryCameraObservationsAndNoTerminationReason()
        {
            DateTimeOffset utc = DateTimeOffset.UnixEpoch.AddSeconds(15);
            byte[] pixels = { 1, 2, 3, 4, 5, 6 };
            CameraObservation image = new CameraObservation("cam.px", 1, 15.0, utc, 0.0005, 2, 1, pixels);
            ISensorFrame frame = new SensorFrame<CameraObservation>(
                "run", "cam.px", "cam_px", 1, 150, 15.0, utc, SensorFrameStatus.Valid, "argus", image);
            Dictionary<string, IDeviceKind> kinds = new Dictionary<string, IDeviceKind> { ["cam.px"] = CameraKind.Instance };

            PbGateway.Observation message = GatewayMapper.ToProto(
                new Observation("run", 150, 15.0, utc, "basilisk", new[] { frame }, ObservationStatus.Ok), kinds);
            PbGateway.ObservationFrame sent = message.Frames.Single();
            Assert.That((sent.Frame.Kind, sent.Frame.Payload.Count, sent.Frame.CaptureStateSequence), Is.EqualTo(("camera", 0, 150L)));
            Assert.That((sent.Camera.CameraId, sent.Camera.WidthPx, sent.Camera.HeightPx), Is.EqualTo(("cam.px", 2, 1)));
            Assert.That(sent.Camera.PixelsRgb8.ToByteArray(), Is.EqualTo(pixels));

            PbGateway.Observation failed = GatewayMapper.ToProto(
                new Observation("run", 150, 15.0, utc, "basilisk", Array.Empty<ISensorFrame>(), ObservationStatus.BackendFailed, "service said: boom"),
                kinds);
            Assert.That(failed.Status, Is.EqualTo(PbGateway.ObservationStatus.BackendFailed));
            Assert.That(failed.ToString(), Does.Not.Contain("boom"));
        }

        // A session that falls 16 messages, or 4 x max_message_bytes, behind overflows.
        [Test]
        public void ASessionThatFallsBehindOverflows()
        {
            PbGateway.ConnectResponse small = new PbGateway.ConnectResponse { RunEnded = new PbGateway.RunEnded { RunId = "run" } };
            GatewaySession byCount = new GatewaySession("controller-1", 16, long.MaxValue);
            for (int index = 0; index < 16; index++)
            {
                byCount.Send(small);
            }

            Assert.That(byCount.Overflowed.IsCancellationRequested, Is.False);
            byCount.Send(small);
            Assert.That(byCount.Overflowed.IsCancellationRequested, Is.True);

            int size = small.CalculateSize();
            GatewaySession byBytes = new GatewaySession("controller-2", 16, 3L * size);
            byBytes.Send(small);
            byBytes.Send(small);
            byBytes.Send(small);
            Assert.That(byBytes.Overflowed.IsCancellationRequested, Is.False);
            byBytes.Send(small);
            Assert.That(byBytes.Overflowed.IsCancellationRequested, Is.True);
        }

        // While a call runs, the gateway's clock reads the call's arrival time.
        [Test]
        public async Task ACallRunsAtItsArrivalTime()
        {
            GatewayDispatcher dispatcher = new GatewayDispatcher();
            TimeSpan arrival = TimeSpan.FromSeconds(1234);
            Task<TimeSpan> seen = dispatcher.Post(arrival, dispatcher.GatewayClock);
            dispatcher.Pump(TimeSpan.Zero, () => true);
            Assert.That(await seen, Is.EqualTo(arrival));
            Assert.That(dispatcher.GatewayClock(), Is.LessThan(arrival));

            dispatcher.Complete();
            Assert.ThrowsAsync<GatewayStoppedException>(() => dispatcher.Post(arrival, () => 0));
        }

        private static async Task<List<PbGateway.Observation>> StepAsync(Controller controller, PbGateway.Observation from, int steps)
        {
            List<PbGateway.Observation> observations = new List<PbGateway.Observation>();
            PbGateway.Observation current = from;
            for (int index = 0; index < steps; index++)
            {
                await controller.SendAsync(Step(current), (ulong)(1000 + index));
                current = (await controller.NextAsync()).Observation;
                Assert.That(current.Sequence, Is.EqualTo(from.Sequence + index + 1));
                PbGateway.CallResult result = (await controller.NextAsync()).Result;
                Assert.That((result.Accepted, result.RequestId), Is.EqualTo((true, (ulong)(1000 + index))), result.Refusal);
                observations.Add(current);
            }

            return observations;
        }

        private static PbGateway.ConnectRequest Claim() =>
            new PbGateway.ConnectRequest { Claim = new PbGateway.ClaimAuthority() };

        private static PbGateway.ConnectRequest Reset(int seed) =>
            new PbGateway.ConnectRequest { Reset = new PbGateway.ResetRun { Seed = seed } };

        // The empty command for the step after this observation: no output.
        private static PbGateway.ConnectRequest Step(PbGateway.Observation observation) =>
            new PbGateway.ConnectRequest
            {
                Step = new PbGateway.StepCommand
                {
                    Command = new PbGateway.CommandForStep
                    {
                        Sequence = observation.Sequence,
                        ApplyAtSimulationTimeS = observation.SimulationTimeS,
                        Commands = new PbSim.DeviceCommandSet()
                    }
                }
            };

        private static SimulationConfiguration Configuration(double heartbeatTimeoutSeconds) =>
            new SimulationConfiguration("gateway-test", DateTimeOffset.UnixEpoch, 0.1, heartbeatTimeoutWallSeconds: heartbeatTimeoutSeconds);

        private static HostOptions Options(params string[] args)
        {
            Assert.That(HostOptions.TryParse(args, out HostOptions options, out string error), Is.True, error);
            return options;
        }

        // A gateway over the analytic engine, served on a free loopback port. In lockstep a
        // background thread plays the host loop's part, running calls as they come; otherwise the
        // test plays it (HostStep, HostCallAsync).
        private sealed class Served : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stopPumping = new CancellationTokenSource();
            private Task _pumping = Task.CompletedTask;

            private Served(GatewayServer server, SimulationGateway gateway)
            {
                Server = server;
                Gateway = gateway;
            }

            public GatewayServer Server { get; }
            public GatewayHub Hub => Server.Hub;
            public SimulationGateway Gateway { get; }
            public List<CommandRecord> Commands { get; } = new List<CommandRecord>();

            public static async Task<Served> StartAsync(StepDriver driver, double heartbeatTimeoutSeconds, bool pumpOnItsOwn = true)
            {
                SimulationConfiguration configuration = Configuration(heartbeatTimeoutSeconds);
                HostOptions options = Options("--step-driver", driver == StepDriver.Controller ? "controller" : "host");
                GatewayDispatcher dispatcher = new GatewayDispatcher();
                GatewayHub hub = new GatewayHub(
                    dispatcher,
                    configuration,
                    driver,
                    Program.SessionRefusal(configuration, options),
                    driver == StepDriver.Host ? Program.CommandWindow(configuration, options) : (TimeSpan?)null,
                    TestProfileId);
                GatewayServer server = await GatewayServer.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), hub);
                SimulationGateway gateway = new SimulationGateway(
                    new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0), stepDriver: driver, clock: dispatcher.GatewayClock);
                Served served = new Served(server, gateway);
                hub.Attach(gateway);
                gateway.CommandLogged += served.Commands.Add;
                gateway.Reset(0, configuration);
                if (pumpOnItsOwn)
                {
                    served._pumping = Task.Factory.StartNew(
                        () =>
                        {
                            while (!served._stopPumping.IsCancellationRequested)
                            {
                                dispatcher.Pump(TimeSpan.FromMilliseconds(20), () => false);
                            }
                        },
                        TaskCreationOptions.LongRunning);
                }

                return served;
            }

            public GrpcChannel Channel() =>
                GrpcChannel.ForAddress($"http://{Server.Endpoint}", new GrpcChannelOptions
                {
                    MaxReceiveMessageSize = (int)Hub.MaxMessageBytes
                });

            // Reads gateway state on the loop's thread.
            public Task<T> OnLoop<T>(Func<SimulationGateway, T> read) => PumpedAsync(Hub.Dispatcher.Post(Hub.Dispatcher.Now, () => read(Gateway)));

            // As the host loop between steps: runs calls until the holder's command for the next step
            // is in or the window ends, then steps.
            public void HostStep(TimeSpan window)
            {
                Hub.Dispatcher.Pump(window, () => Gateway.AuthorityHolder == null || Hub.SubmittedSequence == Gateway.NextSequence);
                Gateway.CheckHeartbeat();
                Gateway.Step();
            }

            // Sends a call and runs the loop until its result is back.
            public async Task<PbGateway.CallResult> HostCallAsync(Controller controller, PbGateway.ConnectRequest request, bool accepted = true)
            {
                await controller.SendAsync(request, 7);
                Task<PbGateway.ConnectResponse> next = controller.NextAsync();
                while (!next.IsCompleted)
                {
                    Hub.Dispatcher.Pump(TimeSpan.FromMilliseconds(20), () => next.IsCompleted);
                }

                PbGateway.ConnectResponse response = await next;
                Assert.That(response.MessageCase, Is.EqualTo(PbGateway.ConnectResponse.MessageOneofCase.Result), response.ToString());
                PbGateway.CallResult result = response.Result;
                Assert.That(result.Accepted, Is.EqualTo(accepted), result.Refusal);
                return result;
            }

            private async Task<T> PumpedAsync<T>(Task<T> task)
            {
                if (!_pumping.IsCompleted)
                {
                    return await task;
                }

                while (!task.IsCompleted)
                {
                    Hub.Dispatcher.Pump(TimeSpan.FromMilliseconds(20), () => task.IsCompleted);
                }

                return await task;
            }

            private bool _disposed;

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stopPumping.Cancel();
                await _pumping;
                Hub.Dispatcher.Complete();
                await Server.DisposeAsync();
            }
        }

        // One controller session over the wire.
        private sealed class Controller : IAsyncDisposable
        {
            private readonly GrpcChannel _channel;
            private readonly AsyncDuplexStreamingCall<PbGateway.ConnectRequest, PbGateway.ConnectResponse> _call;
            private ulong _nextRequestId = 1;
            private bool _disposed;

            private Controller(GrpcChannel channel, AsyncDuplexStreamingCall<PbGateway.ConnectRequest, PbGateway.ConnectResponse> call)
            {
                _channel = channel;
                _call = call;
            }

            public PbGateway.SessionStarted Session { get; private set; }

            // host: the test plays the host loop, so it pumps until SessionStarted arrives.
            public static async Task<Controller> ConnectAsync(Served served, Served host = null)
            {
                GrpcChannel channel = served.Channel();
                Controller controller = new Controller(channel, Call(channel));
                Task<PbGateway.ConnectResponse> first = controller.NextAsync();
                while (host != null && !first.IsCompleted)
                {
                    host.Hub.Dispatcher.Pump(TimeSpan.FromMilliseconds(20), () => first.IsCompleted);
                }

                controller.Session = (await first).SessionStarted;
                Assert.That(controller.Session, Is.Not.Null);
                return controller;
            }

            public static AsyncDuplexStreamingCall<PbGateway.ConnectRequest, PbGateway.ConnectResponse> Call(GrpcChannel channel) =>
                channel.CreateCallInvoker().AsyncDuplexStreamingCall(
                    new Method<PbGateway.ConnectRequest, PbGateway.ConnectResponse>(
                        MethodType.DuplexStreaming,
                        "argus.gateway.v1.ControllerGatewayService",
                        "Connect",
                        Marshallers.Create(request => request.ToByteArray(), PbGateway.ConnectRequest.Parser.ParseFrom),
                        Marshallers.Create(response => response.ToByteArray(), PbGateway.ConnectResponse.Parser.ParseFrom)),
                    null,
                    new CallOptions());

            public Task SendAsync(PbGateway.ConnectRequest request, ulong requestId)
            {
                request.RequestId = requestId;
                return _call.RequestStream.WriteAsync(request);
            }

            public async Task<PbGateway.ConnectResponse> NextAsync()
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(Wait);
                Assert.That(await _call.ResponseStream.MoveNext(timeout.Token), Is.True, "the session ended");
                return _call.ResponseStream.Current;
            }

            // True when the server ended the session with OK and nothing more.
            public async Task<bool> EndedAsync()
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(Wait);
                return !await _call.ResponseStream.MoveNext(timeout.Token);
            }

            public Task ExpectAcceptedAsync(PbGateway.ConnectRequest request) => ExpectAsync(request, true);

            public Task<PbGateway.CallResult> ExpectRefusedAsync(PbGateway.ConnectRequest request) => ExpectAsync(request, false);

            private async Task<PbGateway.CallResult> ExpectAsync(PbGateway.ConnectRequest request, bool accepted)
            {
                ulong id = _nextRequestId++;
                await SendAsync(request, id);
                PbGateway.ConnectResponse response = await NextAsync();
                Assert.That(response.MessageCase, Is.EqualTo(PbGateway.ConnectResponse.MessageOneofCase.Result));
                Assert.That((response.Result.RequestId, response.Result.Accepted), Is.EqualTo((id, accepted)), response.Result.Refusal);
                return response.Result;
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                await _call.RequestStream.CompleteAsync();
                _call.Dispose();
                _channel.Dispose();
            }
        }
    }
}
