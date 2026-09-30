using System;
using System.Globalization;

namespace Argus.Simulation.Host
{
    public sealed class HostOptions
    {
        public const string EndpointVariable = "ARGUS_BASILISK_ENDPOINT";
        public const string DefaultEndpoint = "127.0.0.1:50051";

        public const string Usage =
            "Usage: Argus.Simulation.Host [--duration-s <seconds>] [--real-time-factor <factor>] " +
            "[--basilisk-endpoint <host:port>]\n" +
            "  --duration-s         Simulated seconds to run (default 60).\n" +
            "  --real-time-factor   0 runs unpaced (default); above 0 Basilisk paces each step.\n" +
            "  --basilisk-endpoint  Basilisk service (default $" + EndpointVariable + ", then " + DefaultEndpoint + ").";

        private HostOptions(double durationSeconds, double realTimeFactor, Uri basiliskAddress)
        {
            DurationSeconds = durationSeconds;
            RealTimeFactor = realTimeFactor;
            BasiliskAddress = basiliskAddress;
        }

        public double DurationSeconds { get; }
        public double RealTimeFactor { get; }
        public Uri BasiliskAddress { get; }

        // The endpoint comes from the flag, then the environment, then the default.
        public static bool TryParse(string[] args, out HostOptions options, out string error)
        {
            options = null;
            double durationSeconds = 60.0;
            double realTimeFactor = 0.0;
            string endpoint = null;

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
                    default:
                        error = $"Unknown option {name}.";
                        return false;
                }
            }

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
            }

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = DefaultEndpoint;
            }

            if (!HasExplicitPort(endpoint) ||
                !Uri.TryCreate("http://" + endpoint, UriKind.Absolute, out Uri address) ||
                address.Port < 1 ||
                address.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(address.Query))
            {
                error = $"The Basilisk endpoint must be host:port with port 1-65535, got '{endpoint}'.";
                return false;
            }

            options = new HostOptions(durationSeconds, realTimeFactor, address);
            error = null;
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
