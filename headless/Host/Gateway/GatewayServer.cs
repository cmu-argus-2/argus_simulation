using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Argus.Simulation.Core;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PbGateway = Argus.Contracts.Gateway.V1;

namespace Argus.Simulation.Host
{
    // Serves argus/gateway/v1 (CON-3, HOST-6) to HIL adapters and external controllers: native gRPC
    // over cleartext HTTP/2 on a loopback address, one bidirectional stream per controller session.
    // SimulationGateway is single-threaded (CORE-9), so every link call is posted to the
    // GatewayDispatcher and runs on the host loop's thread between steps, never inside a gateway
    // event. Controllers get controller-visible observations only, never truth (D7), and a terminal
    // observation's status but not its reason.
    internal sealed class GatewayServer : IAsyncDisposable
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

        private readonly WebApplication _application;

        private GatewayServer(WebApplication application, GatewayHub hub)
        {
            _application = application;
            Hub = hub;
        }

        public GatewayHub Hub { get; }

        // The bound address; its port is the OS's choice when the endpoint asked for port 0.
        public IPEndPoint Endpoint { get; private set; }

        // Binds the endpoint and starts serving. Throws when the address is in use.
        public static async Task<GatewayServer> StartAsync(IPEndPoint endpoint, GatewayHub hub)
        {
            // As StateStreamServer: no configuration sources, no logging providers, and Program
            // owns the process's signals.
            WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.Services.AddSingleton<IHostLifetime, ProgramOwnedLifetime>();
            builder.WebHost.UseKestrelCore();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(endpoint, listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddRoutingCore();
            builder.Services.AddGrpc(grpc =>
            {
                grpc.MaxSendMessageSize = checked((int)hub.MaxMessageBytes);
                grpc.MaxReceiveMessageSize = checked((int)GatewayMapper.DefaultMaxMessageBytes);
            });
            builder.Services.AddSingleton(hub);

            WebApplication application = builder.Build();
            application.MapGrpcService<ControllerGatewayService>();
            GatewayServer server = new GatewayServer(application, hub);
            await application.StartAsync().ConfigureAwait(false);
            string address = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>().Addresses.First();
            server.Endpoint = new IPEndPoint(endpoint.Address, new Uri(address).Port);
            return server;
        }

        // Ends every session with OK once its queued messages are sent (at most StopTimeout), then
        // stops listening. Program completes the dispatcher first, so no call runs meanwhile.
        public async ValueTask DisposeAsync()
        {
            Hub.Stop();
            using CancellationTokenSource timeout = new CancellationTokenSource(StopTimeout);
            await _application.StopAsync(timeout.Token).ConfigureAwait(false);
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Runs link calls on the thread that owns the gateway. The host loop calls Pump between steps;
    // a call posted from a gRPC thread waits until then. While a call runs, the gateway's clock reads
    // the call's arrival time, so the gateway's own step time never counts as a controller's silence
    // (CORE-9's known limit).
    internal sealed class GatewayDispatcher
    {
        private readonly BlockingCollection<Call> _calls = new BlockingCollection<Call>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan? _arrival;

        public TimeSpan Now => _clock.Elapsed;

        // The gateway's clock (SimulationGateway's clock argument).
        public TimeSpan GatewayClock() => _arrival ?? _clock.Elapsed;

        public Task<T> Post<T>(TimeSpan arrival, Func<T> run)
        {
            Call<T> call = new Call<T>(arrival, run);
            try
            {
                _calls.Add(call);
            }
            catch (InvalidOperationException)
            {
                call.Refuse();
            }

            return call.Task;
        }

        // Runs every call already posted, then waits up to maxWait for more until done() holds.
        // Only the gateway's thread calls it.
        public void Pump(TimeSpan maxWait, Func<bool> done)
        {
            TimeSpan deadline = _clock.Elapsed + maxWait;
            while (true)
            {
                while (_calls.TryTake(out Call queued))
                {
                    Run(queued);
                }

                TimeSpan remaining = deadline - _clock.Elapsed;
                if (done() || remaining <= TimeSpan.Zero)
                {
                    return;
                }

                if (_calls.TryTake(out Call next, remaining))
                {
                    Run(next);
                }
            }
        }

        // No call runs after this: queued and later ones fail with GatewayStoppedException.
        public void Complete()
        {
            _calls.CompleteAdding();
            while (_calls.TryTake(out Call queued))
            {
                queued.Refuse();
            }
        }

        private void Run(Call call)
        {
            _arrival = call.Arrival;
            try
            {
                call.Run();
            }
            finally
            {
                _arrival = null;
            }
        }

        private abstract class Call
        {
            protected Call(TimeSpan arrival)
            {
                Arrival = arrival;
            }

            public TimeSpan Arrival { get; }

            public abstract void Run();

            public abstract void Refuse();
        }

        private sealed class Call<T> : Call
        {
            private readonly Func<T> _run;
            private readonly TaskCompletionSource<T> _completion =
                new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Call(TimeSpan arrival, Func<T> run)
                : base(arrival)
            {
                _run = run;
            }

            public Task<T> Task => _completion.Task;

            public override void Run()
            {
                try
                {
                    _completion.SetResult(_run());
                }
                catch (Exception exception)
                {
                    _completion.SetException(exception);
                }
            }

            public override void Refuse() => _completion.TrySetException(new GatewayStoppedException());
        }
    }

    internal sealed class GatewayStoppedException : Exception
    {
        public GatewayStoppedException()
            : base("The host has stopped serving the gateway.")
        {
        }
    }

    // The controller sessions and what they do to the gateway. Every member but Stop, Stopping and
    // the settings runs on the gateway's thread (GatewayDispatcher).
    internal sealed class GatewayHub
    {
        // How long a stopping host keeps sending each session the messages it already queued, such
        // as the last observations and RunEnded.
        public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

        // Argus.Contracts/README.md "Controller gateway": the most unsent messages per session, and
        // their most bytes as a multiple of max_message_bytes.
        public const int MaxUnsentMessages = 16;
        public const int MaxUnsentMessageSizes = 4;

        private readonly List<GatewaySession> _sessions = new List<GatewaySession>();
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly SimulationConfiguration _configuration;
        private readonly IReadOnlyDictionary<string, IDeviceKind> _kinds;
        private SimulationGateway _gateway;
        private PbGateway.ConnectResponse _latest;
        private string _runId;
        private long _nextControllerNumber = 1;

        // sessionRefusal: why the host refuses controller sessions, or null (Program.SessionRefusal).
        public GatewayHub(
            GatewayDispatcher dispatcher,
            SimulationConfiguration configuration,
            StepDriver stepDriver,
            string sessionRefusal)
        {
            Dispatcher = dispatcher;
            _configuration = configuration;
            _kinds = GatewayMapper.Kinds(configuration);
            StepDriver = stepDriver;
            SessionRefusal = sessionRefusal;
            MaxMessageBytes = GatewayMapper.MaxMessageBytes(configuration);
        }

        public GatewayDispatcher Dispatcher { get; }
        public StepDriver StepDriver { get; }
        public string SessionRefusal { get; }
        public long MaxMessageBytes { get; }
        public CancellationToken Stopping => _stopping.Token;

        // The step k of the last command the authority holder submitted and the gateway accepted, so
        // an owner-stepped loop can step as soon as command k is in (Program).
        public long SubmittedSequence { get; private set; } = -1;

        // Sends the gateway's observations and run ends to every session. Call once, before the
        // first Reset.
        public void Attach(SimulationGateway gateway)
        {
            _gateway = gateway;
            gateway.RunStarted += (run, sensors) =>
            {
                _runId = run.RunId;
                SubmittedSequence = -1;
            };
            gateway.ObservationPublished += observation =>
            {
                _latest = new PbGateway.ConnectResponse { Observation = GatewayMapper.ToProto(observation, _kinds) };
                SendToAll(_latest);
            };
            gateway.RunEnded += () =>
            {
                _latest = null;
                SendToAll(new PbGateway.ConnectResponse { RunEnded = new PbGateway.RunEnded { RunId = _runId ?? "" } });
            };
        }

        // Opens a session: SessionStarted, then the running run's latest observation.
        public GatewaySession Open()
        {
            GatewaySession session = new GatewaySession(
                $"controller-{_nextControllerNumber++}",
                MaxUnsentMessages,
                MaxUnsentMessageSizes * MaxMessageBytes);
            _sessions.Add(session);
            session.Send(new PbGateway.ConnectResponse
            {
                SessionStarted = new PbGateway.SessionStarted
                {
                    ControllerId = session.ControllerId,
                    StepDriver = GatewayMapper.ToProto(StepDriver),
                    HeartbeatTimeoutWallS = _configuration.HeartbeatTimeoutWallSeconds,
                    MaxMessageBytes = (ulong)MaxMessageBytes
                }
            });
            if (_latest != null && _gateway.IsRunning)
            {
                session.Send(_latest);
            }

            return session;
        }

        // Ends a session: it stops receiving, and it loses authority if it held it (failsafe).
        public void Close(GatewaySession session, string reason)
        {
            _sessions.Remove(session);
            _gateway.Revoke(session.ControllerId, reason);
        }

        // Runs one call and answers it after anything it published (CON-3 session rules).
        public void Handle(GatewaySession session, PbGateway.ConnectRequest request)
        {
            string refusal = Run(session.ControllerId, request);
            session.Send(new PbGateway.ConnectResponse
            {
                Result = new PbGateway.CallResult
                {
                    RequestId = request.RequestId,
                    Accepted = refusal == null,
                    Refusal = refusal ?? ""
                }
            });
        }

        public void Stop() => _stopping.Cancel();

        // Null when accepted, else why not. A refused gateway call is already logged by the gateway.
        private string Run(string controllerId, PbGateway.ConnectRequest request)
        {
            string refusal;
            switch (request.CallCase)
            {
                case PbGateway.ConnectRequest.CallOneofCase.Claim:
                    return _gateway.TryClaim(controllerId, out refusal) ? null : refusal;
                case PbGateway.ConnectRequest.CallOneofCase.Release:
                    return _gateway.TryRelease(controllerId, out refusal) ? null : refusal;
                case PbGateway.ConnectRequest.CallOneofCase.Heartbeat:
                    return _gateway.TryHeartbeat(controllerId, out refusal) ? null : refusal;
                case PbGateway.ConnectRequest.CallOneofCase.Submit:
                {
                    DeviceCommandSet? command = GatewayMapper.TryDecode(request.Submit.Command, "submit.command", out refusal);
                    if (command == null)
                    {
                        return refusal;
                    }

                    if (!_gateway.TrySubmit(controllerId, command.Value, out refusal))
                    {
                        return refusal;
                    }

                    SubmittedSequence = command.Value.Sequence;
                    return null;
                }

                case PbGateway.ConnectRequest.CallOneofCase.Step:
                {
                    DeviceCommandSet? command = GatewayMapper.TryDecode(request.Step.Command, "step.command", out refusal);
                    if (command == null)
                    {
                        return refusal;
                    }

                    try
                    {
                        // A backend failure returns a terminal observation: the step was accepted.
                        _gateway.Step(controllerId, command.Value);
                        return null;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
                    {
                        return exception.Message;
                    }
                }

                case PbGateway.ConnectRequest.CallOneofCase.Reset:
                    return Reset(controllerId, request.Reset);
                default:
                    return "call: required field is not set";
            }
        }

        // Only the holder of a running lockstep run, with a seed; the scenario stays the host's.
        private string Reset(string controllerId, PbGateway.ResetRun reset)
        {
            if (StepDriver != StepDriver.Controller)
            {
                return "Only the authority holder of a lockstep run may reset it; the host steps this run.";
            }

            if (!reset.HasSeed)
            {
                return "reset.seed: required field is not set";
            }

            if (!_gateway.IsRunning || _gateway.AuthorityHolder != controllerId)
            {
                return "Only the controller holding authority may reset the run.";
            }

            try
            {
                _gateway.Reset(reset.Seed, _configuration);
                return null;
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                // The cause can be backend failure text, which stays in the host's log (D7).
                Console.Error.WriteLine($"A controller's reset failed: {exception.Message}");
                return "The reset failed; the cause is in the host's log.";
            }
        }

        private void SendToAll(PbGateway.ConnectResponse message)
        {
            foreach (GatewaySession session in _sessions)
            {
                session.Send(message);
            }
        }
    }

    // One controller session's outgoing messages, bounded in count and bytes. A session that falls
    // behind is not slowed down to wait for: it overflows, and the server ends it.
    internal sealed class GatewaySession
    {
        private readonly Channel<(PbGateway.ConnectResponse Message, int Size)> _out =
            Channel.CreateUnbounded<(PbGateway.ConnectResponse, int)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _overflow = new CancellationTokenSource();
        private readonly object _lock = new object();
        private readonly int _maxMessages;
        private readonly long _maxBytes;
        private int _unsentMessages;
        private long _unsentBytes;

        public GatewaySession(string controllerId, int maxMessages, long maxBytes)
        {
            ControllerId = controllerId;
            _maxMessages = maxMessages;
            _maxBytes = maxBytes;
        }

        public string ControllerId { get; }
        public CancellationToken Overflowed => _overflow.Token;

        public void Send(PbGateway.ConnectResponse message)
        {
            int size = message.CalculateSize();
            lock (_lock)
            {
                if (_overflow.IsCancellationRequested)
                {
                    return;
                }

                if (_unsentMessages >= _maxMessages || _unsentBytes + size > _maxBytes)
                {
                    _overflow.Cancel();
                    return;
                }

                _unsentMessages++;
                _unsentBytes += size;
            }

            _out.Writer.TryWrite((message, size));
        }

        public void Complete() => _out.Writer.TryComplete();

        // Writes every message in order until the session completes or the token ends it.
        public async Task WriteAllAsync(IServerStreamWriter<PbGateway.ConnectResponse> writer, CancellationToken token)
        {
            await foreach ((PbGateway.ConnectResponse message, int size) in _out.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await writer.WriteAsync(message, token).ConfigureAwait(false);
                lock (_lock)
                {
                    _unsentMessages--;
                    _unsentBytes -= size;
                }
            }
        }
    }

    internal sealed class ControllerGatewayService : PbGateway.ControllerGatewayService.ControllerGatewayServiceBase
    {
        private readonly GatewayHub _hub;

        public ControllerGatewayService(GatewayHub hub)
        {
            _hub = hub;
        }

        // One session: the server's messages flow out on their own task while calls are handled one
        // at a time, in order. The session ends when the client ends its stream, the host stops
        // (OK), or the session falls behind (RESOURCE_EXHAUSTED); every end revokes its authority.
        public override async Task Connect(
            IAsyncStreamReader<PbGateway.ConnectRequest> requestStream,
            IServerStreamWriter<PbGateway.ConnectResponse> responseStream,
            ServerCallContext context)
        {
            if (_hub.SessionRefusal != null)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, _hub.SessionRefusal));
            }

            GatewayDispatcher dispatcher = _hub.Dispatcher;
            GatewaySession session;
            try
            {
                session = await dispatcher.Post(dispatcher.Now, _hub.Open).ConfigureAwait(false);
            }
            catch (GatewayStoppedException)
            {
                return;
            }

            // Reading stops when the host stops; writing goes on until the queue is sent, so a session
            // still gets the run's last observations and its RunEnded.
            using CancellationTokenSource ending = CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken, _hub.Stopping, session.Overflowed);
            using CancellationTokenSource abandoned = CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken, session.Overflowed);
            Task writing = session.WriteAllAsync(responseStream, abandoned.Token);
            string endReason = "the controller ended its session";
            try
            {
                while (await requestStream.MoveNext(ending.Token).ConfigureAwait(false))
                {
                    PbGateway.ConnectRequest request = requestStream.Current;
                    TimeSpan arrival = dispatcher.Now;
                    await dispatcher.Post(arrival, () =>
                    {
                        _hub.Handle(session, request);
                        return true;
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ending.IsCancellationRequested)
            {
                endReason = session.Overflowed.IsCancellationRequested
                    ? "the controller's session fell behind"
                    : "the controller's session ended";
            }
            catch (GatewayStoppedException)
            {
                endReason = "the host stopped";
            }
            catch (Exception) when (context.CancellationToken.IsCancellationRequested)
            {
                endReason = "the controller's connection closed";
            }
            finally
            {
                try
                {
                    await dispatcher.Post(dispatcher.Now, () =>
                    {
                        _hub.Close(session, endReason);
                        return true;
                    }).ConfigureAwait(false);
                }
                catch (GatewayStoppedException)
                {
                    // The run is over; there is no authority left to revoke.
                }

                session.Complete();
            }

            try
            {
                await writing.WaitAsync(GatewayHub.DrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A client that stopped reading gets no more; ending the call drops the rest.
                abandoned.Cancel();
            }
            catch (Exception) when (abandoned.IsCancellationRequested)
            {
                // The session ended with messages unsent: the client left or fell behind.
            }

            if (session.Overflowed.IsCancellationRequested)
            {
                throw new RpcException(new Status(
                    StatusCode.ResourceExhausted,
                    $"The session fell more than {GatewayHub.MaxUnsentMessages} messages or " +
                    $"{GatewayHub.MaxUnsentMessageSizes} x max_message_bytes behind; its authority was revoked."));
            }
        }
    }
}
