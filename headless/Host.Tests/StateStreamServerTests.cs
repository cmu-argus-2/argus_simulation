using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Argus.Simulation.Core;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Host.Tests
{
    // The running state stream (HOST-3): decimation per client, and a step loop that a stalled
    // client never delays.
    internal sealed class StateStreamServerTests
    {
        // Serves the stream over the analytic gateway while one client never reads and another
        // reads everything. The step loop keeps its pace, and the reader gets distinct, increasing
        // pairs of one generation, no faster than the rate: 400 Hz is a 2.5 ms period, which the
        // whole-millisecond timer must round up, not down.
        [TestCase(1000.0)]
        [TestCase(400.0)]
        public async Task AStalledClientNeverDelaysTheStepLoop(double rateHz)
        {
            Assert.That(HostOptions.TryStreamPeriod(rateHz, out TimeSpan period), Is.True);
            await using StateStreamServer server = await StateStreamServer.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), period);
            SimulationGateway gateway = new SimulationGateway(new AnalyticSimulationEngine(500_000.0, 51.6, 0.0, 0.0));
            gateway.RunStarted += (run, sensors) => server.Publisher.BeginGeneration(run);
            gateway.StateProduced += server.Publisher.PublishState;
            gateway.SensorOutputProduced += server.Publisher.PublishSensorOutput;
            gateway.Reset(0, new SimulationConfiguration("stream-test", DateTimeOffset.UnixEpoch, 0.1));

            // Separate connections, so the stalled stream's flow control can't hold the reader back.
            using GrpcChannel stalledChannel = Channel(server);
            using GrpcChannel readerChannel = Channel(server);
            using AsyncServerStreamingCall<PbStream.SubscribeResponse> stalled = Subscribe(stalledChannel);
            using AsyncServerStreamingCall<PbStream.SubscribeResponse> reader = Subscribe(readerChannel);
            List<PbStream.SubscribeResponse> received = new List<PbStream.SubscribeResponse>();
            Task reading = Task.Run(async () =>
            {
                while (await reader.ResponseStream.MoveNext(CancellationToken.None))
                {
                    received.Add(reader.ResponseStream.Current);
                }
            });

            // Long enough at this rate to fill the stalled stream's HTTP/2 window many times over.
            TimeSpan slowestStep = TimeSpan.Zero;
            Stopwatch running = Stopwatch.StartNew();
            long steps = 0;
            while (running.Elapsed < TimeSpan.FromSeconds(1.5))
            {
                long start = Stopwatch.GetTimestamp();
                gateway.Step();
                TimeSpan step = Stopwatch.GetElapsedTime(start);
                slowestStep = step > slowestStep ? step : slowestStep;
                steps++;
            }

            TimeSpan elapsed = running.Elapsed;
            server.Publisher.Stop();
            await reading.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(slowestStep, Is.LessThan(TimeSpan.FromMilliseconds(250)));
            Assert.That(steps, Is.GreaterThan(1000));
            Assert.That(received.Count, Is.GreaterThan(10));
            Assert.That(received.Count, Is.LessThanOrEqualTo(elapsed.TotalSeconds * rateHz + 2));
            for (int i = 0; i < received.Count; i++)
            {
                Assert.That(received[i].RunGeneration, Is.Not.Empty.And.EqualTo(received[0].RunGeneration));
                Assert.That(received[i].SensorOutput.SpacecraftSequence, Is.EqualTo(received[i].State.Spacecraft.Sequence));
                if (i > 0)
                {
                    Assert.That(received[i].State.Spacecraft.Sequence, Is.GreaterThan(received[i - 1].State.Spacecraft.Sequence));
                }
            }
        }

        // The timer takes whole milliseconds from 1 ms, so faster or vanishingly slow rates are bad
        // input, refused with the flag's name before anything starts.
        [TestCase("2000")]
        [TestCase("1e-300")]
        [TestCase("0")]
        public void ARateTheTimerCannotKeepIsRefused(string rate)
        {
            Assert.That(HostOptions.TryParse(new[] { "--stream-rate-hz", rate }, out _, out string error), Is.False);
            Assert.That(error, Does.StartWith("--stream-rate-hz"));
        }

        // Configuration cannot add a listener beside the loopback endpoint: the web host reads no
        // ASPNETCORE_ variables or appsettings.json.
        [Test]
        public async Task ConfigurationCannotBindAnotherEndpoint()
        {
            const string Variable = "ASPNETCORE_Kestrel__Endpoints__Extra__Url";
            int extraPort = FreePort();
            Environment.SetEnvironmentVariable(Variable, $"http://127.0.0.1:{extraPort}");
            try
            {
                Assert.That(HostOptions.TryStreamPeriod(10.0, out TimeSpan period), Is.True);
                await using StateStreamServer server = await StateStreamServer.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), period);
                using System.Net.Sockets.TcpClient probe = new System.Net.Sockets.TcpClient();
                Assert.That(
                    () => probe.Connect(IPAddress.Loopback, extraPort),
                    Throws.InstanceOf<System.Net.Sockets.SocketException>());
            }
            finally
            {
                Environment.SetEnvironmentVariable(Variable, null);
            }
        }

        // HOST-3's end-to-end check, run by hand against a running Basilisk service (default
        // 127.0.0.1:50051, or $ARGUS_BASILISK_ENDPOINT): the host with a no-sensor run file streams
        // states that carry EnvironmentState.
        [Test, Explicit("Needs the Argus.Basilisk service running")]
        public async Task ANoSensorRunAgainstTheServiceStreamsStatesWithEnvironment()
        {
            JsonObject run = JsonNode.Parse(File.ReadAllText(HostOptions.DefaultRunConfigurationPath)).AsObject();
            run["devices"] = new JsonArray();
            string path = Path.Combine(Path.GetTempPath(), $"argus-stream-run-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, run.ToJsonString());
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Loopback, FreePort());
            try
            {
                Task<int> host = Task.Run(() => Program.Main(new[]
                {
                    "--run-config", path, "--duration-s", "1200", "--stream-endpoint", endpoint.ToString(), "--stream-rate-hz", "20"
                }));

                using GrpcChannel channel = GrpcChannel.ForAddress($"http://{endpoint}");
                PbStream.SubscribeResponse first = null;
                Stopwatch waiting = Stopwatch.StartNew();
                while (first == null && waiting.Elapsed < TimeSpan.FromSeconds(30) && !host.IsCompleted)
                {
                    try
                    {
                        using AsyncServerStreamingCall<PbStream.SubscribeResponse> call = Subscribe(channel);
                        if (await call.ResponseStream.MoveNext(CancellationToken.None))
                        {
                            first = call.ResponseStream.Current;
                        }
                    }
                    catch (RpcException exception) when (exception.StatusCode == StatusCode.Unavailable)
                    {
                        await Task.Delay(100);
                    }
                }

                Assert.That(await host, Is.EqualTo(0));
                Assert.That(first, Is.Not.Null);
                Assert.That(first.RunGeneration, Is.Not.Empty);
                Assert.That(first.State.DynamicsBackend, Is.EqualTo("basilisk"));
                Assert.That(first.State.Spacecraft.EarthFixedFrame, Is.EqualTo(Contracts.Sim.V1.ReferenceFrame.Itrf93));
                Assert.That(first.State.Environment, Is.Not.Null);
                Assert.That(first.State.Environment.HasSpacecraftShadowFactor, Is.True);
                Assert.That(first.SensorOutput.Frames, Is.Empty);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static GrpcChannel Channel(StateStreamServer server) =>
            GrpcChannel.ForAddress($"http://{server.Endpoint}");

        // The host generates only the server side of argus/stream/v1, so the test calls the method
        // directly, as Unity's generated client does.
        private static AsyncServerStreamingCall<PbStream.SubscribeResponse> Subscribe(GrpcChannel channel) =>
            channel.CreateCallInvoker().AsyncServerStreamingCall(
                new Method<PbStream.SubscribeRequest, PbStream.SubscribeResponse>(
                    MethodType.ServerStreaming,
                    "argus.stream.v1.StateStreamService",
                    "Subscribe",
                    Marshallers.Create(request => request.ToByteArray(), PbStream.SubscribeRequest.Parser.ParseFrom),
                    Marshallers.Create(response => response.ToByteArray(), PbStream.SubscribeResponse.Parser.ParseFrom)),
                null,
                new CallOptions(),
                new PbStream.SubscribeRequest());

        private static int FreePort()
        {
            System.Net.Sockets.TcpListener listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
