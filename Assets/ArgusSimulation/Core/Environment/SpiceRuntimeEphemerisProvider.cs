using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace Argus.Simulation.Core
{
    // Owns one persistent SPICE worker. Queries are evaluated by SPICE at simulation time;
    // no trajectory or environment samples are loaded from a recorded scenario file.
    public sealed class SpiceRuntimeEphemerisProvider : IEphemerisProvider, IDisposable
    {
        private const int ResponseValueCount = 22;
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5.0);
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10.0);

        private readonly object _sync = new object();
        private readonly DateTimeOffset _epochUtc;
        private readonly Process _process;
        private long _nextRequestId;
        private bool _disposed;

        public SpiceRuntimeEphemerisProvider(DateTimeOffset epochUtc, string launcherPath)
        {
            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                throw new ArgumentException("A SPICE runtime launcher is required.", nameof(launcherPath));
            }

            string fullPath = Path.GetFullPath(launcherPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("SPICE runtime launcher was not found.", fullPath);
            }

            _epochUtc = epochUtc.ToUniversalTime();
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fullPath,
                    Arguments = "--epoch " + QuoteArgument(
                        _epochUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)),
                    WorkingDirectory = Path.GetDirectoryName(fullPath),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    LastError = string.IsNullOrWhiteSpace(LastError)
                        ? args.Data
                        : LastError + Environment.NewLine + args.Data;
                }
            };

            try
            {
                if (!_process.Start())
                {
                    throw new InvalidOperationException("SPICE runtime did not start.");
                }

                _process.BeginErrorReadLine();
                string ready = ReadLine(StartupTimeout);
                if (ready != "READY\targus.spice.runtime\t1\tJ2000\tITRF93")
                {
                    throw new InvalidOperationException(
                        $"SPICE runtime returned an incompatible handshake: '{ready ?? "<no response>"}'. " +
                        LastError);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string SourceName => "spice-runtime";
        public DateTimeOffset EpochUtc => _epochUtc;
        public string LastError { get; private set; }

        public bool TryGetSample(double simulationTimeSeconds, out EphemerisSample sample)
        {
            sample = default;
            if (_disposed || !IsFinite(simulationTimeSeconds) || simulationTimeSeconds < 0.0)
            {
                return false;
            }

            lock (_sync)
            {
                if (_disposed || _process.HasExited)
                {
                    LastError = LastError ?? "SPICE runtime is not running.";
                    return false;
                }

                long requestId = _nextRequestId++;
                try
                {
                    _process.StandardInput.WriteLine(
                        "SAMPLE\t" + requestId.ToString(CultureInfo.InvariantCulture) + "\t" +
                        simulationTimeSeconds.ToString("R", CultureInfo.InvariantCulture));
                    _process.StandardInput.Flush();
                    string response = ReadLine(QueryTimeout);
                    if (TryParseSampleResponse(
                            response,
                            requestId,
                            simulationTimeSeconds,
                            _epochUtc,
                            out sample,
                            out string error))
                    {
                        LastError = null;
                        return true;
                    }

                    LastError = error;
                    return false;
                }
                catch (Exception error) when (
                    error is IOException ||
                    error is InvalidOperationException ||
                    error is ObjectDisposedException ||
                    error is TimeoutException)
                {
                    LastError = error.Message;
                    return false;
                }
            }
        }

        internal static bool TryParseSampleResponse(
            string response,
            long expectedRequestId,
            double simulationTimeSeconds,
            DateTimeOffset epochUtc,
            out EphemerisSample sample,
            out string error)
        {
            sample = default;
            error = null;
            if (string.IsNullOrWhiteSpace(response))
            {
                error = "SPICE runtime returned no response.";
                return false;
            }

            string[] fields = response.Split('\t');
            if (fields.Length >= 3 && fields[0] == "ERROR")
            {
                error = fields[2];
                return false;
            }

            if (fields.Length != ResponseValueCount + 2 ||
                fields[0] != "SAMPLE" ||
                !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long requestId) ||
                requestId != expectedRequestId)
            {
                error = "SPICE runtime returned a malformed or out-of-sequence response.";
                return false;
            }

            double[] values = new double[ResponseValueCount];
            for (int index = 0; index < values.Length; index++)
            {
                if (!double.TryParse(
                        fields[index + 2],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out values[index]) ||
                    !IsFinite(values[index]))
                {
                    error = $"SPICE runtime returned an invalid numeric field at index {index}.";
                    return false;
                }
            }

            sample = new EphemerisSample(
                simulationTimeSeconds,
                epochUtc.AddSeconds(simulationTimeSeconds),
                values[0],
                Matrix3d.FromRowMajor(Slice(values, 1, 9)),
                Matrix3d.FromRowMajor(Slice(values, 10, 9)),
                new Vector3d(values[19], values[20], values[21]));
            if (!sample.IsValid)
            {
                error = "SPICE runtime returned an invalid environment sample.";
                sample = default;
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        _process.StandardInput.WriteLine("QUIT");
                        _process.StandardInput.Flush();
                        if (!_process.WaitForExit(2000))
                        {
                            _process.Kill();
                        }
                    }
                }
                catch (Exception)
                {
                    // Best-effort shutdown during simulation/editor teardown.
                }
                finally
                {
                    _process?.Dispose();
                }
            }
        }

        private string ReadLine(TimeSpan timeout)
        {
            Task<string> read = _process.StandardOutput.ReadLineAsync();
            if (!read.Wait(timeout))
            {
                throw new TimeoutException($"SPICE runtime did not respond within {timeout.TotalSeconds:F0} seconds.");
            }

            return read.Result;
        }

        private static string QuoteArgument(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static double[] Slice(double[] values, int start, int count)
        {
            double[] result = new double[count];
            Array.Copy(values, start, result, 0, count);
            return result;
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
