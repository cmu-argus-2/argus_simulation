using System;
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

        // The checked-in P0 run file, copied next to the host binary.
        public static readonly string DefaultRunConfigurationPath =
            Path.Combine(AppContext.BaseDirectory, "runs", "p0-default.json");

        public const string Usage =
            "Usage: Argus.Simulation.Host [--duration-s <seconds>] [--real-time-factor <factor>] " +
            "[--basilisk-endpoint <host:port>] [--run-config <path>] [--record <dir>] " +
            "[--stream-endpoint <host:port>] [--stream-rate-hz <hz>]\n" +
            "  --duration-s         Simulated seconds to run (default 60).\n" +
            "  --real-time-factor   0 runs unpaced (default); above 0 Basilisk paces each step.\n" +
            "  --basilisk-endpoint  Basilisk service (default $" + EndpointVariable + ", then " + DefaultEndpoint + ").\n" +
            "  --run-config         Run configuration file (default runs/p0-default.json).\n" +
            "  --record             Write dataset v1 under this directory, one <run_id>_<segment> folder per run (default: no recording).\n" +
            "  --stream-endpoint    Loopback address the state stream to Unity listens on (default $" + StreamEndpointVariable + ", then " + DefaultStreamEndpoint + ").\n" +
            "  --stream-rate-hz     Most state-stream sends per second of wall-clock time, per client (default $" + StreamRateVariable + ", then 10).";

        private HostOptions(
            double durationSeconds,
            double realTimeFactor,
            Uri basiliskAddress,
            string runConfigurationPath,
            string recordDirectory,
            IPEndPoint streamEndpoint,
            double streamRateHz)
        {
            DurationSeconds = durationSeconds;
            RealTimeFactor = realTimeFactor;
            BasiliskAddress = basiliskAddress;
            RunConfigurationPath = runConfigurationPath;
            RecordDirectory = recordDirectory;
            StreamEndpoint = streamEndpoint;
            StreamRateHz = streamRateHz;
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

            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
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
                    default:
                        error = $"Unknown option {name}.";
                        return false;
                }
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
            if (streamRate != null && (!TryParseDouble(streamRate, out streamRateHz) || streamRateHz <= 0.0))
            {
                error = $"The stream rate must be a positive number of Hz, got '{streamRate}'.";
                return false;
            }

            options = new HostOptions(
                durationSeconds, realTimeFactor, address, runConfigurationPath, recordDirectory, stream, streamRateHz);
            error = null;
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
