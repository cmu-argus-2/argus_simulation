using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Argus.Simulation.Core;
using PbStream = Argus.Contracts.Stream.V1;

namespace Argus.Simulation.Host
{
    // Plays a recorded run into the state stream (HOST-7): one new Replay generation, then each
    // recorded step's state and sensor output, as the gateway's events publish a live run's. No
    // engine, gateway or controller runs, and nothing is rendered or recorded. The host's own
    // stopwatch paces it, never a Basilisk clock (D2).
    internal static class RunReplay
    {
        // Publishes state k at (t_k - t_0) / speed of wall-clock time after state 0. A replay that
        // falls behind publishes each state as soon as it can and skips none; the stream decimates
        // to its rate as in a live run. Each published state also goes to published. Returns the
        // number of states.
        public static long Play(
            RecordedRun run,
            StateStreamPublisher publisher,
            double speed,
            CancellationToken cancellation,
            Action<SimulationState> published = null)
        {
            if (!(speed > 0.0) || double.IsInfinity(speed))
            {
                throw new ArgumentOutOfRangeException(nameof(speed), speed, "The replay speed must be finite and above 0.");
            }

            publisher.BeginGeneration(run.Configuration, PbStream.RunSource.Replay);
            Stopwatch clock = Stopwatch.StartNew();
            double? firstTime = null;
            long count = 0;
            foreach ((SimulationState state, SensorOutputSet output) in run.ReadSteps())
            {
                double time = state.Spacecraft.SimulationTimeSeconds;
                firstTime ??= time;
                double dueSeconds = (time - firstTime.Value) / speed;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    double remaining = dueSeconds - clock.Elapsed.TotalSeconds;
                    if (remaining <= 0.0)
                    {
                        break;
                    }

                    // At most a second at a time, so any wait fits WaitOne's range.
                    cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(remaining, 1.0)));
                }

                publisher.PublishState(state);
                publisher.PublishSensorOutput(output);
                published?.Invoke(state);
                count++;
            }

            if (count == 0)
            {
                throw new InvalidDataException($"{run.Directory}: states.jsonl has no states to replay.");
            }

            return count;
        }
    }
}
