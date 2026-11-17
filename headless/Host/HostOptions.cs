using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;

namespace Argus.Simulation.Host
{
    public sealed class HostOptions
    {
        public const string EndpointVariable = "ARGUS_BASILISK_ENDPOINT";
        public const string DefaultEndpoint = "127.0.0.1:50051";

        // The state stream to Unity (CON-1, Argus.Contracts/README.md Endpoints).
        public const string StreamEndpointVariable = "ARGUS_STREAM_ENDPOINT";
        public const string DefaultStreamEndpoint = "127.0.0.1:50052";
        public const string StreamRateVariable = "ARGUS_STREAM_RATE_HZ";
        public const double DefaultStreamRateHz = 10.0;

        // The stream's send timer (PeriodicTimer) takes whole milliseconds, from 1 ms to
        // uint.MaxValue - 1 ms, so the rate is at most 1000 Hz and at least one send per 49.7 days.
        public const double MaximumStreamRateHz = 1000.0;
        private const double MaximumStreamPeriodMilliseconds = uint.MaxValue - 1.0;

        // The checked-in P0 run file, copied next to the host binary.
        public static readonly string DefaultRunConfigurationPath =
            Path.Combine(AppContext.BaseDirectory, "runs", "p0-default.json");

        public const string Usage =
            "Usage: Argus.Simulation.Host [--duration-s <seconds>] [--real-time-factor <factor>] " +
            "[--basilisk-endpoint <host:port>] [--run-config <path>] [--record <dir>] " +
            "[--stream-endpoint <host:port>] [--stream-rate-hz <hz>]\n" +
            "       Argus.Simulation.Host --replay <run dir> [--replay-speed <factor>] " +
            "[--stream-endpoint <host:port>] [--stream-rate-hz <hz>]\n" +
            "  --duration-s         Simulated seconds to run (default 60).\n" +
            "  --real-time-factor   0 runs unpaced (default); above 0 Basilisk paces each step.\n" +
            "  --basilisk-endpoint  Basilisk service (default $" + EndpointVariable + ", then " + DefaultEndpoint + ").\n" +
            "  --run-config         Run configuration file (default runs/p0-default.json).\n" +
            "  --record             Write dataset v1 under this directory, one <run_id>_<segment> folder per run (default: no recording).\n" +
            "  --stream-endpoint    Loopback address the state stream to Unity listens on (default $" + StreamEndpointVariable + ", then " + DefaultStreamEndpoint + ").\n" +
            "  --stream-rate-hz     Most state-stream sends per second of wall-clock time, per client (default $" + StreamRateVariable + ", then 10).\n" +
            "  --replay             Replay one recorded run directory (<run_id>_<segment>, as --record writes it) over the state stream,\n" +
            "                       with no Basilisk service, gateway or controller; takes none of the live-run options above.\n" +
            "  --replay-speed       Replay pace as a multiple of recorded simulation time, above 0 (default 1).";

        // The options that configure a live run, which a replay rejects.
        private static readonly string[] LiveRunOptions =
            { "--duration-s", "--real-time-factor", "--basilisk-endpoint", "--run-config", "--record" };

        private HostOptions(
            double durationSeconds,
            double realTimeFactor,
            Uri basiliskAddress,
            string runConfigurationPath,
            string recordDirectory,
            IPEndPoint streamEndpoint,
            double streamRateHz,
            TimeSpan streamPeriod,
            string replayDirectory,
            double replaySpeed)
        {
            DurationSeconds = durationSeconds;
            RealTimeFactor = realTimeFactor;
            BasiliskAddress = basiliskAddress;
            RunConfigurationPath = runConfigurationPath;
            RecordDirectory = recordDirectory;
            StreamEndpoint = streamEndpoint;
            StreamRateHz = streamRateHz;
            StreamPeriod = streamPeriod;
            ReplayDirectory = replayDirectory;
            ReplaySpeed = replaySpeed;
        }

        public double DurationSeconds { get; }
        public double RealTimeFactor { get; }
        public Uri BasiliskAddress { get; }
        public string RunConfigurationPath { get; }

        // Null when the run is not recorded.
        public string RecordDirectory { get; }

        // A loopback address: v1 serves the stream to this machine only, in cleartext h2c.
        public IPEndPoint StreamEndpoint { get; }

        // Finite and positive; it paces delivery only, never physics or sensor sampling.
        public double StreamRateHz { get; }

        // The least time between two sends to one client: 1 / StreamRateHz rounded up to whole
        // milliseconds, so a client never receives faster than StreamRateHz.
        public TimeSpan StreamPeriod { get; }

        // Null for a live run; otherwise the recorded run directory the host replays (HOST-7).
        public string ReplayDirectory { get; }

        // Finite and above 0: wall-clock seconds per recorded simulation second is 1 / ReplaySpeed.
        public double ReplaySpeed { get; }

        // Each endpoint and the stream rate come from the flag, then the environment, then the
        // default.
        public static bool TryParse(string[] args, out HostOptions options, out string error)
        {
            options = null;
            double durationSeconds = 60.0;
            double realTimeFactor = 0.0;
            string endpoint = null;
            string runConfigurationPath = DefaultRunConfigurationPath;
            string recordDirectory = null;
            string streamEndpoint = null;
            string streamRate = null;
            string replayDirectory = null;
            double replaySpeed = 1.0;
            HashSet<string> given = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                given.Add(name);
                if (i + 1 >= args.Length)
                {
                    error = $"Missing value for {name}.";
                    return false;
                }

                string value = args[++i];
                switch (name)
                {
                    case "--duration-s":
                        if (!TryParseDouble(value, out durationSeconds) || durationSeconds <= 0.0)
                        {
                            error = "--duration-s must be a positive number.";
                            return false;
                        }

                        break;
                    case "--real-time-factor":
                        if (!TryParseDouble(value, out realTimeFactor) || realTimeFactor < 0.0)
                        {
                            error = "--real-time-factor must be a non-negative number.";
                            return false;
                        }

                        break;
                    case "--basilisk-endpoint":
                        endpoint = value;
                        break;
                    case "--run-config":
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            error = "--run-config must be a file path.";
                            return false;
                        }

                        runConfigurationPath = value;
                        break;
                    case "--record":
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            error = "--record must be a directory path.";
                            return false;
                        }

                        recordDirectory = value;
                        break;
                    case "--stream-endpoint":
                        streamEndpoint = value;
                        break;
                    case "--stream-rate-hz":
                        streamRate = value;
                        break;
                    case "--replay":
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            error = "--replay must be a directory path.";
                            return false;
                        }

                        replayDirectory = value;
                        break;
                    case "--replay-speed":
                        if (!TryParseDouble(value, out replaySpeed) || replaySpeed <= 0.0)
                        {
                            error = "--replay-speed must be a number above 0.";
                            return false;
                        }

                        break;
                    default:
                        error = $"Unknown option {name}.";
                        return false;
                }
            }

            if (replayDirectory != null)
            {
                foreach (string option in LiveRunOptions)
                {
                    if (given.Contains(option))
                    {
                        error = $"{option} configures a live run; a replay (--replay) takes none.";
                        return false;
                    }
                }
            }
            else if (given.Contains("--replay-speed"))
            {
                error = "--replay-speed needs --replay.";
                return false;
            }

            endpoint = FirstSet(endpoint, EndpointVariable, DefaultEndpoint);
            if (!TryParseEndpoint(endpoint, out Uri address))
            {
                error = $"The Basilisk endpoint must be host:port with port 1-65535, got '{endpoint}'.";
                return false;
            }

            streamEndpoint = FirstSet(streamEndpoint, StreamEndpointVariable, DefaultStreamEndpoint);
            if (!TryParseEndpoint(streamEndpoint, out Uri streamAddress) || !TryLoopback(streamAddress, out IPEndPoint stream))
            {
                error = $"The stream endpoint must be a loopback host:port with port 1-65535, got '{streamEndpoint}'.";
                return false;
            }

            streamRate = FirstSet(streamRate, StreamRateVariable, null);
            double streamRateHz = DefaultStreamRateHz;
            if ((streamRate != null && !TryParseDouble(streamRate, out streamRateHz)) ||
                !TryStreamPeriod(streamRateHz, out TimeSpan streamPeriod))
            {
                error = "--stream-rate-hz (or $" + StreamRateVariable + ") must be a number of Hz above 0 and at most " +
                    $"{MaximumStreamRateHz:R}, at least one send per 49.7 days, got '{streamRate}'.";
                return false;
            }

            options = new HostOptions(
                durationSeconds,
                realTimeFactor,
                address,
                runConfigurationPath,
                recordDirectory,
                stream,
                streamRateHz,
                streamPeriod,
                replayDirectory,
                replaySpeed);
            error = null;
            return true;
        }

        // The send period of a rate, rounded up to whole milliseconds; false for a rate the timer
        // cannot keep.
        internal static bool TryStreamPeriod(double rateHz, out TimeSpan period)
        {
            period = default;
            if (!(rateHz > 0.0) || rateHz > MaximumStreamRateHz)
            {
                return false;
            }

            double milliseconds = Math.Ceiling(1000.0 / rateHz);
            if (milliseconds > MaximumStreamPeriodMilliseconds)
            {
                return false;
            }

            period = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        // The flag's value, else the environment variable's, else the default.
        private static string FirstSet(string flag, string variable, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(flag))
            {
                return flag;
            }

            string environment = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrWhiteSpace(environment) ? fallback : environment;
        }

        // Bare host:port, or [ipv6]:port, with no scheme or path (Argus.Contracts/README.md Endpoints).
        private static bool TryParseEndpoint(string endpoint, out Uri address) =>
            Uri.TryCreate("http://" + endpoint, UriKind.Absolute, out address) &&
            HasExplicitPort(endpoint) &&
            address.Port >= 1 &&
            address.AbsolutePath == "/" &&
            string.IsNullOrEmpty(address.Query);

        // localhost or a loopback IP address; the host never serves another interface in v1.
        private static bool TryLoopback(Uri address, out IPEndPoint endpoint)
        {
            endpoint = null;
            IPAddress ip = string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                ? IPAddress.Loopback
                : IPAddress.TryParse(address.DnsSafeHost, out IPAddress parsed) ? parsed : null;
            if (ip == null || !IPAddress.IsLoopback(ip))
            {
                return false;
            }

            endpoint = new IPEndPoint(ip, address.Port);
            return true;
        }

        // host:port or [ipv6]:port, matching the service's parse_endpoint. Uri alone cannot tell an
        // explicit :80 from a missing port.
        private static bool HasExplicitPort(string endpoint)
        {
            int separator = endpoint.LastIndexOf(':');
            if (separator <= 0 || separator == endpoint.Length - 1)
            {
                return false;
            }

            if (endpoint[0] == '[' && endpoint[separator - 1] != ']')
            {
                return false;
            }

            for (int i = separator + 1; i < endpoint.Length; i++)
            {
                if (endpoint[i] < '0' || endpoint[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseDouble(string value, out double result) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            !double.IsNaN(result) &&
            !double.IsInfinity(result);
    }
}
