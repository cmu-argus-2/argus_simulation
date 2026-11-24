using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Argus.Simulation.Host
{
    // The Basilisk service as a child of the host (--start-basilisk), so a run needs no service
    // started by hand. It runs the README's setup steps first: it regenerates the service's contract
    // code and checks (or fetches) the run's kernel set. Then it serves with --stop-on-stdin-eof. The
    // host holds the service's standard input, so the service stops when the host disposes this or
    // exits for any reason, even when killed.
    internal sealed class BasiliskServiceProcess : IDisposable
    {
        public const string DirectoryName = "Argus.Basilisk";
        private const string ReadyLinePrefix = "BasiliskSimulationService listening on ";
        private const string OutputPrefix = "[basilisk] ";

        // Importing Basilisk takes a few seconds, longer on the first import after an install.
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

        private readonly Process _process;

        private BasiliskServiceProcess(Process process)
        {
            _process = process;
        }

        // Starts the service on endpoint and returns once it is listening; null when a service is
        // already listening there, which the host then uses as it is.
        public static BasiliskServiceProcess Start(
            string serviceDirectory,
            Uri endpoint,
            string kernelSetId,
            CancellationToken cancellation)
        {
            string address = $"{endpoint.Host}:{endpoint.Port}";
            if (IsListening(endpoint))
            {
                Console.WriteLine($"A Basilisk service is already listening on {address}; using it instead of starting one.");
                return null;
            }

            string directory = serviceDirectory ?? FindDirectory() ?? throw new InvalidOperationException(
                $"--start-basilisk: found no {DirectoryName} directory above {AppContext.BaseDirectory} or " +
                $"{Environment.CurrentDirectory}; pass --basilisk-dir.");
            string python = PythonPath(directory) ?? throw new InvalidOperationException(
                $"--start-basilisk: {Path.Combine(directory, ".venv")} has no Python. Run `uv sync --frozen --all-extras` " +
                $"in {directory} first (README, Set up and run the Basilisk service).");

            RunToEnd(python, directory, cancellation, Path.Combine("scripts", "generate_protos.py"));
            RunToEnd(python, directory, cancellation, Path.Combine("scripts", "fetch_kernels.py"), kernelSetId);

            TaskCompletionSource<bool> listening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Process process = Launch(python, directory, line =>
            {
                if (line.StartsWith(ReadyLinePrefix, StringComparison.Ordinal))
                {
                    listening.TrySetResult(true);
                }
            }, "-m", "argus_basilisk", "--endpoint", address, "--stop-on-stdin-eof");

            BasiliskServiceProcess service = new BasiliskServiceProcess(process);
            try
            {
                Task exited = process.WaitForExitAsync(cancellation);
                Task finished = Task.WhenAny(listening.Task, exited, Task.Delay(ReadyTimeout, cancellation))
                    .GetAwaiter().GetResult();
                cancellation.ThrowIfCancellationRequested();
                if (finished == exited)
                {
                    throw new InvalidOperationException(
                        $"--start-basilisk: the Basilisk service exited with code {process.ExitCode} before listening on " +
                        $"{address} (its output is above).");
                }

                if (finished != listening.Task)
                {
                    throw new InvalidOperationException(
                        $"--start-basilisk: the Basilisk service was not listening on {address} after {ReadyTimeout.TotalSeconds:R} s.");
                }
            }
            catch
            {
                service.Dispose();
                throw;
            }

            Console.WriteLine($"Started the Basilisk service on {address} (pid {process.Id}); it stops when the host exits.");
            return service;
        }

        // Closing standard input stops the service (__main__.py --stop-on-stdin-eof); one that has
        // not stopped by StopTimeout is killed.
        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit((int)StopTimeout.TotalMilliseconds))
                    {
                        Console.Error.WriteLine($"{OutputPrefix}did not stop within {StopTimeout.TotalSeconds:R} s; killing it");
                        _process.Kill(entireProcessTree: true);
                    }
                }

                // Drains the last output lines.
                _process.WaitForExit();
            }
            finally
            {
                _process.Dispose();
            }
        }

        // The Argus.Basilisk directory (with its pyproject.toml) beside or above the host's binary,
        // else beside or above the current directory; null when there is none.
        internal static string FindDirectory()
        {
            foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                for (DirectoryInfo directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                {
                    string candidate = Path.Combine(directory.FullName, DirectoryName);
                    if (File.Exists(Path.Combine(candidate, "pyproject.toml")))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static string PythonPath(string directory)
        {
            string posix = Path.Combine(directory, ".venv", "bin", "python");
            string windows = Path.Combine(directory, ".venv", "Scripts", "python.exe");
            return File.Exists(posix) ? posix : File.Exists(windows) ? windows : null;
        }

        private static bool IsListening(Uri endpoint)
        {
            try
            {
                using TcpClient client = new TcpClient(endpoint.HostNameType == UriHostNameType.IPv6
                    ? AddressFamily.InterNetworkV6
                    : AddressFamily.InterNetwork);
                return client.ConnectAsync(endpoint.DnsSafeHost, endpoint.Port).Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        // A setup script, run to its end; a non-zero exit fails the start, with its output above.
        private static void RunToEnd(string python, string directory, CancellationToken cancellation, params string[] arguments)
        {
            using Process process = Launch(python, directory, null, arguments);
            process.StandardInput.Close();
            using (cancellation.Register(() => KillQuietly(process)))
            {
                process.WaitForExit();
            }

            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"--start-basilisk: {arguments[0]} exited with code {process.ExitCode} (its output is above).");
            }
        }

        // Starts python with its output on the host's console, each line prefixed with [basilisk]
        // (standard error to standard error), and its standard input left to the caller.
        private static Process Launch(string python, string directory, Action<string> onOutputLine, params string[] arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo(python)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            // Unbuffered, so each line arrives when the service prints it.
            start.Environment["PYTHONUNBUFFERED"] = "1";
            // gRPC's own log at errors only: otherwise its fork-handler notes (INFO) reach the
            // host's standard error, and Unity's Console as warnings.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GRPC_VERBOSITY")))
            {
                start.Environment["GRPC_VERBOSITY"] = "ERROR";
            }

            Process process = new Process { StartInfo = start };
            process.OutputDataReceived += (sender, line) =>
            {
                if (line.Data != null)
                {
                    Console.WriteLine(OutputPrefix + line.Data);
                    onOutputLine?.Invoke(line.Data);
                }
            };
            process.ErrorDataReceived += (sender, line) =>
            {
                if (line.Data != null)
                {
                    Console.Error.WriteLine(OutputPrefix + line.Data);
                }
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        private static void KillQuietly(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It has already exited.
            }
        }
    }
}
