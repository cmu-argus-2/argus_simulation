using System;
using System.Threading;
using System.Threading.Tasks;
using Argus.Simulation.Core;
using Cysharp.Net.Http;
using Grpc.Core;
using Grpc.Net.Client;
using UnityEngine;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Unity
{
    public enum StateStreamStatus
    {
        Stopped = 0,
        Connecting = 1,
        Streaming = 2,
        Disconnected = 3
    }

    // Follows the headless host's state stream (CON-1, G2): native gRPC over cleartext HTTP/2 with
    // YetAnotherHttpHandler, subscribed on a background task. The stream is lossy: the reader keeps
    // only the latest received pair, and each frame publishes it on the main thread, so a frame that
    // falls behind skips pairs rather than catching up in a burst. In order: StateProduced with each SimulationState (ISimulationStateSource, so a
    // SimulationRunner can follow it, UNITY-6) and OutputProduced with its latest-per-sensor
    // SensorOutputSet. When the host stops or restarts, the client reconnects and reports Status
    // instead of throwing. The endpoint is -stream-endpoint, then $ARGUS_STREAM_ENDPOINT, then the
    // Inspector value (Argus.Contracts/README.md Endpoints).
    public sealed class StateStreamClient : MonoBehaviour, ISimulationStateSource
    {
        public const string EndpointCommandLineOption = "-stream-endpoint";
        public const string EndpointVariable = "ARGUS_STREAM_ENDPOINT";
        public const string DefaultEndpoint = "127.0.0.1:50052";

        [Tooltip("The host's state stream, bare host:port (loopback in v1).")]
        [SerializeField] private string endpoint = DefaultEndpoint;
        [SerializeField, Min(0.1f)] private float reconnectDelaySeconds = 1.0f;

        private Received _latest;
        private CancellationTokenSource _cancellation;
        private int _status;
        private string _statusMessage = "";
        private string _lastLogged = "";
        private long _subscriptions;
        private long _publishedSubscription;
        private long _lastSequence = -1;

        public event Action<SimulationState> StateProduced;
        public event Action<SensorOutputSet> OutputProduced;

        // Raised before the first pair of a new run generation (a new run or Reset on the host):
        // interpolation and history from the earlier run must be cleared. A reconnect to the same
        // generation continues it.
        public event Action<string> HistoryReset;

        public StateStreamStatus Status => (StateStreamStatus)Volatile.Read(ref _status);

        // Why the client is not streaming, for the dashboard; empty while it streams.
        public string StatusMessage => Volatile.Read(ref _statusMessage);

        public string Endpoint => endpoint;

        // The generation of the last published pair, or null before the first.
        public string RunGeneration { get; private set; }

        private void OnEnable()
        {
            // The host keeps running while Unity is unfocused, so Unity keeps following it.
            Application.runInBackground = true;
            endpoint = ResolveEndpoint(endpoint);
            Volatile.Write(ref _latest, null);
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            Task.Run(() => SubscribeAsync("http://" + endpoint, token));
        }

        private void OnDisable()
        {
            // The task ends on its own; nothing waits for the network on the main thread.
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            Volatile.Write(ref _latest, null);
            SetStatus(StateStreamStatus.Stopped, "", CancellationToken.None);
        }

        private void Update()
        {
            Received received = Interlocked.Exchange(ref _latest, null);
            // A cancelled subscription's last pair can land after a newer one's: never publish it.
            if (received == null || received.Subscription < _publishedSubscription)
            {
                return;
            }

            _publishedSubscription = received.Subscription;
            if (received.Pair.RunGeneration != RunGeneration)
            {
                RunGeneration = received.Pair.RunGeneration;
                _lastSequence = -1;
                HistoryReset?.Invoke(RunGeneration);
            }

            // Sequence only increases within a generation, across reconnects too, so a pair is
            // never published twice.
            long sequence = received.Pair.State.Spacecraft.Sequence;
            if (sequence <= _lastSequence)
            {
                return;
            }

            _lastSequence = sequence;
            try
            {
                StateProduced?.Invoke(received.Pair.State);
            }
            finally
            {
                OutputProduced?.Invoke(received.Pair.Output);
            }
        }

        private async Task SubscribeAsync(string address, CancellationToken token)
        {
            // One channel serves every subscription; a failed connection is retried on the next call.
            using GrpcChannel channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new YetAnotherHttpHandler { Http2Only = true },
                DisposeHttpClient = true
            });
            PbStream.StateStreamService.StateStreamServiceClient client = new PbStream.StateStreamService.StateStreamServiceClient(channel);
            while (!token.IsCancellationRequested)
            {
                long subscription = Interlocked.Increment(ref _subscriptions);
                SetStatus(StateStreamStatus.Connecting, $"Connecting to {address}", token);
                try
                {
                    using AsyncServerStreamingCall<PbStream.SubscribeResponse> call =
                        client.Subscribe(new PbStream.SubscribeRequest(), cancellationToken: token);
                    while (await call.ResponseStream.MoveNext(token).ConfigureAwait(false))
                    {
                        // A bad response ends this subscription rather than being published.
                        StreamPair pair = StateStreamMapper.ToCore(call.ResponseStream.Current);
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        Volatile.Write(ref _latest, new Received(subscription, pair));
                        SetStatus(StateStreamStatus.Streaming, "", token);
                    }

                    SetStatus(StateStreamStatus.Disconnected, $"The host at {address} ended the stream", token);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (RpcException exception)
                {
                    SetStatus(StateStreamStatus.Disconnected, $"No stream from {address}: {exception.Status.Detail}", token);
                }
                catch (FormatException exception)
                {
                    SetStatus(StateStreamStatus.Disconnected, $"Rejected a response from {address}: {exception.Message}", token);
                }
                catch (Exception exception)
                {
                    // Anything else also ends only this subscription; the client keeps reconnecting.
                    SetStatus(StateStreamStatus.Disconnected, $"The stream from {address} failed: {exception.Message}", token);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(reconnectDelaySeconds), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        // Logs each new Streaming or Disconnected status once, from any thread, so a host that stays
        // down logs one line, not one per retry. A cancelled subscription reports nothing, so a
        // disabled client stays Stopped.
        private void SetStatus(StateStreamStatus status, string message, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            Interlocked.Exchange(ref _status, (int)status);
            Interlocked.Exchange(ref _statusMessage, message);
            string line = $"State stream: {status}{(message.Length > 0 ? " - " + message : "")}";
            if ((status == StateStreamStatus.Streaming || status == StateStreamStatus.Disconnected) &&
                Interlocked.Exchange(ref _lastLogged, line) != line)
            {
                // A status line, not an error: its call stack would only bury it.
                Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", line);
            }
        }

        private static string ResolveEndpoint(string configured)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], EndpointCommandLineOption, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            string variable = Environment.GetEnvironmentVariable(EndpointVariable);
            return string.IsNullOrWhiteSpace(variable) ? configured : variable;
        }

        private sealed class Received
        {
            public Received(long subscription, StreamPair pair)
            {
                Subscription = subscription;
                Pair = pair;
            }

            public long Subscription { get; }
            public StreamPair Pair { get; }
        }
    }
}
