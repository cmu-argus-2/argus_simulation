using System;
using System.Collections.Generic;
using System.Threading;
using Argus.Simulation.Core;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Host
{
    // The step thread's side of the state stream (CON-1, Argus.Contracts/README.md "Delivery
    // rules"). The gateway's events feed it: RunStarted opens a generation, StateProduced and then
    // SensorOutputProduced complete one step's pair. It keeps the latest frame of each non-camera
    // sensor and one latest complete pair, replaced each step, so publishing never waits on a
    // client; StateStreamServer's subscriptions read Latest at their own rate.
    internal sealed class StateStreamPublisher
    {
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private StreamGeneration _generation;
        private Dictionary<string, ISensorFrame> _latestFrames;
        private SimulationState? _pendingState;
        private StreamSnapshot _latest;
        private long _version;

        // The latest complete pair of the current generation, or null before its first one. Read
        // from any thread.
        public StreamSnapshot Latest => Volatile.Read(ref _latest);

        // The current generation's ID, or null before the first run.
        public string GenerationId => Volatile.Read(ref _generation)?.Id;

        // Cancelled by Stop: every subscription finishes its stream.
        public CancellationToken Stopping => _stopping.Token;

        // A new ConfigureRun or Reset: a new generation, with no pair and no cached frame, so no
        // subscription ever mixes two runs. Throws for a non-camera sensor whose frames the stream
        // cannot carry as a value map, so the run fails at t_0 instead of a client's first send.
        public void BeginGeneration(SimulationConfiguration run)
        {
            StreamGeneration generation = new StreamGeneration(Guid.NewGuid().ToString("N"), run);
            Volatile.Write(ref _latest, null);
            _pendingState = null;
            _latestFrames = new Dictionary<string, ISensorFrame>(StringComparer.Ordinal);
            Volatile.Write(ref _generation, generation);
        }

        // Step k's truth; its pair is completed by step k's sensor output.
        public void PublishState(SimulationState state)
        {
            RequireGeneration();
            _pendingState = state;
        }

        // Step k's frames: each non-camera frame replaces its sensor's slot, failures included, and
        // the pair (state k, the slots as of step k) replaces the latest one.
        public void PublishSensorOutput(SensorOutputSet output)
        {
            StreamGeneration generation = RequireGeneration();
            if (!_pendingState.HasValue || _pendingState.Value.Spacecraft.Sequence != output.SpacecraftSequence)
            {
                throw new InvalidOperationException(
                    $"Sensor output for step {output.SpacecraftSequence} arrived without its state.");
            }

            foreach (ISensorFrame frame in output.Frames)
            {
                if (generation.Streamed.ContainsKey(frame.SensorId))
                {
                    _latestFrames[frame.SensorId] = frame;
                }
                else if (!generation.Cameras.Contains(frame.SensorId))
                {
                    throw new InvalidOperationException($"Sensor '{frame.SensorId}' is not configured for this run.");
                }
            }

            List<ISensorFrame> frames = new List<ISensorFrame>(_latestFrames.Count);
            foreach (DeviceConfiguration device in generation.Run.Devices)
            {
                if (_latestFrames.TryGetValue(device.Definition.SensorId, out ISensorFrame frame))
                {
                    frames.Add(frame);
                }
            }

            _version++;
            Volatile.Write(ref _latest, new StreamSnapshot(generation, _version, _pendingState.Value, frames.ToArray()));
            _pendingState = null;
        }

        // Ends every subscription's stream.
        public void Stop() => _stopping.Cancel();

        private StreamGeneration RequireGeneration() =>
            _generation ?? throw new InvalidOperationException("The state stream has no run yet.");
    }

    // One ConfigureRun or Reset of the stream: its ID and the run's devices, by role on the stream.
    internal sealed class StreamGeneration
    {
        public StreamGeneration(string id, SimulationConfiguration run)
        {
            Id = id;
            Run = run;
            Dictionary<string, IBackendSensorKind> streamed = new Dictionary<string, IBackendSensorKind>(StringComparer.Ordinal);
            Cameras = new HashSet<string>(StringComparer.Ordinal);
            foreach (DeviceConfiguration device in run.Devices)
            {
                IDeviceKind kind = device.KindDefinition;
                if (kind.Role != DeviceRole.Sensor)
                {
                    continue;
                }

                // Camera images use the render link (UNITY-10), never the state stream.
                if (kind is ICameraKind)
                {
                    Cameras.Add(device.Definition.SensorId);
                }
                else if (kind is IBackendSensorKind backend)
                {
                    streamed.Add(device.Definition.SensorId, backend);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"The state stream has no payload mapping for sensor '{device.Definition.SensorId}' of kind {kind.Name}.");
                }
            }

            Streamed = streamed;
        }

        public string Id { get; }
        public SimulationConfiguration Run { get; }

        // The non-camera sensors the stream carries, with the kind that writes their payloads.
        public IReadOnlyDictionary<string, IBackendSensorKind> Streamed { get; }

        public HashSet<string> Cameras { get; }
    }

    // One complete pair: a step's state and the latest non-camera frames as of that step. Immutable.
    internal sealed class StreamSnapshot
    {
        public StreamSnapshot(StreamGeneration generation, long version, SimulationState state, ISensorFrame[] frames)
        {
            Generation = generation;
            Version = version;
            State = state;
            Frames = frames;
        }

        public StreamGeneration Generation { get; }

        // Increases with every pair, so a subscription never sends the same pair twice.
        public long Version { get; }

        public SimulationState State { get; }
        public IReadOnlyList<ISensorFrame> Frames { get; }

        // Mapped once, by the first subscription to send it, however many clients connect.
        public PbStream.SubscribeResponse Response =>
            LazyInitializer.EnsureInitialized(ref _response, () => StateStreamMapper.ToProto(this));

        private PbStream.SubscribeResponse _response;
    }
}
