using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Host
{
    // Serves argus/stream/v1 to Unity (CON-1, G2): native gRPC over cleartext HTTP/2, on a loopback
    // address only. Each subscription sends the publisher's latest pair at most at the stream rate
    // of wall-clock time, so a slow or stalled client only falls behind itself: the step loop only
    // replaces the latest pair and never waits on a client.
    internal sealed class StateStreamServer : IAsyncDisposable
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

        private readonly WebApplication _application;

        private StateStreamServer(WebApplication application, StateStreamPublisher publisher)
        {
            _application = application;
            Publisher = publisher;
        }

        public StateStreamPublisher Publisher { get; }

        // The bound address; its port is the OS's choice when the endpoint asked for port 0.
        public IPEndPoint Endpoint { get; private set; }

        // Binds the endpoint and starts serving. Throws when the address is in use.
        public static async Task<StateStreamServer> StartAsync(IPEndPoint endpoint, double rateHz)
        {
            StateStreamPublisher publisher = new StateStreamPublisher();
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            // The host's console is the run report; a bind failure surfaces as the exception.
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(endpoint, listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(publisher);
            builder.Services.AddSingleton(new StateStreamRate(TimeSpan.FromSeconds(1.0 / rateHz)));

            WebApplication application = builder.Build();
            application.MapGrpcService<StateStreamService>();
            StateStreamServer server = new StateStreamServer(application, publisher);
            await application.StartAsync().ConfigureAwait(false);
            string address = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>().Addresses.First();
            server.Endpoint = new IPEndPoint(endpoint.Address, new Uri(address).Port);
            return server;
        }

        // Finishes every stream, then stops listening.
        public async ValueTask DisposeAsync()
        {
            Publisher.Stop();
            using CancellationTokenSource timeout = new CancellationTokenSource(StopTimeout);
            await _application.StopAsync(timeout.Token).ConfigureAwait(false);
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal sealed class StateStreamRate
    {
        public StateStreamRate(TimeSpan period)
        {
            Period = period;
        }

        // The least wall-clock time between two sends to one client.
        public TimeSpan Period { get; }
    }

    internal sealed class StateStreamService : PbStream.StateStreamService.StateStreamServiceBase
    {
        private readonly StateStreamPublisher _publisher;
        private readonly StateStreamRate _rate;

        public StateStreamService(StateStreamPublisher publisher, StateStreamRate rate)
        {
            _publisher = publisher;
            _rate = rate;
        }

        // Sends the latest pair at most once per period, never one pair twice and never a burst to
        // catch up (PeriodicTimer drops missed ticks). A subscription follows one generation: it
        // waits for that generation's first pair and finishes when a Reset starts another, so the
        // client resubscribes and never mixes two runs.
        public override async Task Subscribe(
            PbStream.SubscribeRequest request,
            IServerStreamWriter<PbStream.SubscribeResponse> responseStream,
            ServerCallContext context)
        {
            using CancellationTokenSource finished =
                CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _publisher.Stopping);
            using PeriodicTimer timer = new PeriodicTimer(_rate.Period);
            string generation = null;
            long sentVersion = 0;
            try
            {
                do
                {
                    StreamSnapshot snapshot = _publisher.Latest;
                    if (generation != null && (snapshot?.Generation.Id ?? _publisher.GenerationId) != generation)
                    {
                        return;
                    }

                    if (snapshot == null || snapshot.Version == sentVersion)
                    {
                        continue;
                    }

                    generation = snapshot.Generation.Id;
                    await responseStream.WriteAsync(StateStreamMapper.ToProto(snapshot), finished.Token).ConfigureAwait(false);
                    sentVersion = snapshot.Version;
                }
                while (await timer.WaitForNextTickAsync(finished.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (finished.IsCancellationRequested)
            {
                // The client left or the host is stopping: the stream ends normally.
            }
        }
    }
}
