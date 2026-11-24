using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Argus.Simulation.Unity
{
    // Starts the simulation when Play starts, so nothing has to be started by hand: it builds
    // headless/Host, then runs it with --start-basilisk, which starts the Basilisk service too, and
    // --stop-on-stdin-eof. Leaving Play closes the host's standard input, so the host stops its run
    // and then the service; if Unity dies, the closed pipe stops both the same way. The host streams
    // to the StateStreamClient beside this component, which retries until the stream is up. Editor
    // only: a player has no repository to build the host from. The settings are the host's own
    // options (headless/README.md); turn off launchOnPlay to follow a host started by hand instead.
    [DisallowMultipleComponent]
    public sealed class HostLauncher : MonoBehaviour
    {
        public const string HostProject = "headless/Host/Argus.Simulation.Host.csproj";
        public const string HostAssembly = "headless/Host/bin/Debug/net8.0/Argus.Simulation.Host.dll";
        private const int StopTimeoutMilliseconds = 10000;

        [Tooltip("Start headless/Host and the Basilisk service when Play starts, and stop them when it ends.")]
        [SerializeField] private bool launchOnPlay = true;

        [Tooltip("The run file, relative to the repository root (headless/README.md, Run file).")]
        [SerializeField] private string runConfiguration = "headless/Host/runs/p0-default.json";

        [Tooltip("Simulated seconds to run (--duration-s).")]
        [SerializeField, Min(0.1f)] private float durationSeconds = 3600.0f;

        [Tooltip("1 runs in real time; 0 runs as fast as Basilisk can (--real-time-factor).")]
        [SerializeField, Min(0.0f)] private float realTimeFactor = 1.0f;

        [Tooltip("Record dataset v1 under this directory, relative to the repository root (--record). Empty: no recording.")]
        [SerializeField] private string recordDirectory = "";

        [Tooltip("Replay this recorded run directory instead of running one (--replay); the run settings above are then unused.")]
        [SerializeField] private string replayDirectory = "";

        [Tooltip("Replay pace as a multiple of recorded time (--replay-speed).")]
        [SerializeField, Min(0.01f)] private float replaySpeed = 1.0f;

        [Tooltip("The dotnet executable. Empty: $DOTNET_ROOT, then the usual install locations, then dotnet on PATH.")]
        [SerializeField] private string dotnetExecutable = "";

        private readonly object _gate = new object();
        private CancellationTokenSource _cancellation;
        private Process _process;

        private void Start()
        {
            if (!launchOnPlay)
            {
                return;
            }

            if (!Application.isEditor)
            {
                Debug.Log("HostLauncher: a player does not start the host; start headless/Host yourself (README).");
                return;
            }

            string root = RepositoryRoot;
            if (!File.Exists(Path.Combine(root, HostProject)))
            {
                Debug.LogError($"HostLauncher: {HostProject} is not under {root}; start headless/Host yourself.");
                return;
            }

            // Resolved by the client's OnEnable, which runs before any Start.
            StateStreamClient client = GetComponent<StateStreamClient>();
            string streamEndpoint = client != null ? client.Endpoint : StateStreamClient.DefaultEndpoint;
            string dotnet = ResolveDotnet(dotnetExecutable);
            string arguments = HostArguments(root, streamEndpoint);

            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            Task.Run(() => Launch(dotnet, root, arguments, token));
        }

        private void OnDestroy()
        {
            _cancellation?.Cancel();
            Process process;
            lock (_gate)
            {
                process = _process;
                _process = null;
            }

            if (process != null)
            {
                Stop(process);
            }

            _cancellation?.Dispose();
            _cancellation = null;
        }

        private string HostArguments(string root, string streamEndpoint)
        {
            StringBuilder arguments = new StringBuilder();
            Append(arguments, Path.Combine(root, HostAssembly));
            Append(arguments, "--stop-on-stdin-eof");
            Append(arguments, "--stream-endpoint", streamEndpoint);
            if (!string.IsNullOrWhiteSpace(replayDirectory))
            {
                Append(arguments, "--replay", Path.GetFullPath(Path.Combine(root, replayDirectory)));
                Append(arguments, "--replay-speed", Number(replaySpeed));
                return arguments.ToString();
            }

            Append(arguments, "--start-basilisk");
            Append(arguments, "--run-config", Path.GetFullPath(Path.Combine(root, runConfiguration)));
            Append(arguments, "--duration-s", Number(durationSeconds));
            Append(arguments, "--real-time-factor", Number(realTimeFactor));
            if (!string.IsNullOrWhiteSpace(recordDirectory))
            {
                Append(arguments, "--record", Path.GetFullPath(Path.Combine(root, recordDirectory)));
            }

            return arguments.ToString();
        }

        // The repository root: the folder above Assets.
        public static string RepositoryRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // The arguments of the dotnet build that builds the host; incremental, so only the first
        // build after a change takes long.
        public static string BuildArguments => $"build {Quote(HostProject)} --nologo -v q -nodeReuse:false";

        // Runs dotnet to its end, with its output and errors in output; -1 when cancelled.
        public static int RunToEnd(string dotnet, string root, string arguments, StringBuilder output, CancellationToken token)
        {
            using (Process process = StartProcess(dotnet, root, arguments,
                line => { lock (output) { output.AppendLine(line); } },
                line => { lock (output) { output.AppendLine(line); } }))
            {
                process.StandardInput.Close();
                using (token.Register(() => KillQuietly(process)))
                {
                    process.WaitForExit();
                }

                return token.IsCancellationRequested ? -1 : process.ExitCode;
            }
        }

        private void Launch(string dotnet, string root, string arguments, CancellationToken token)
        {
            try
            {
                Debug.Log($"HostLauncher: building {HostProject}");
                StringBuilder buildOutput = new StringBuilder();
                int built = RunToEnd(dotnet, root, BuildArguments, buildOutput, token);
                if (built == -1)
                {
                    return;
                }

                if (built != 0)
                {
                    Debug.LogError($"HostLauncher: dotnet build failed with code {built}:\n{buildOutput}");
                    return;
                }

                Debug.Log($"HostLauncher: starting the host and the Basilisk service: {dotnet} {arguments}");
                // Once Play has ended, the host's report that its run was cancelled is expected, so
                // it is not a warning.
                Process host = StartProcess(dotnet, root, arguments,
                    line => Debug.Log("[host] " + line),
                    line =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            Debug.Log("[host] " + line);
                        }
                        else
                        {
                            Debug.LogWarning("[host] " + line);
                        }
                    });
                host.EnableRaisingEvents = true;
                host.Exited += (sender, eventArgs) => ReportExit(host, token);
                lock (_gate)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _process = host;
                        return;
                    }
                }

                // Play ended while the host was starting.
                Stop(host);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"HostLauncher: could not start the host with {dotnet} ({exception.Message}). Install the .NET 8 SDK, " +
                    "or set Dotnet Executable on this component.");
            }
        }

        private static void ReportExit(Process host, CancellationToken token)
        {
            // Stopped by leaving Play: Stop reports it.
            if (token.IsCancellationRequested)
            {
                return;
            }

            int code = host.ExitCode;
            if (code == 0)
            {
                Debug.Log("HostLauncher: the host finished its run and exited.");
            }
            else
            {
                Debug.LogError($"HostLauncher: the host exited with code {code} (2: bad options or run file; its output is above).");
            }
        }

        // Closing standard input stops the host's run and then its Basilisk service; a host that has not
        // exited by the timeout is killed, and the service then stops on its own closed input.
        private static void Stop(Process host)
        {
            try
            {
                if (!host.HasExited)
                {
                    host.StandardInput.Close();
                    if (!host.WaitForExit(StopTimeoutMilliseconds))
                    {
                        Debug.LogWarning("HostLauncher: the host did not stop in time; killing it.");
                        KillQuietly(host);
                    }
                }

                Debug.Log("HostLauncher: stopped the host and the Basilisk service.");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"HostLauncher: stopping the host: {exception.Message}");
            }
            finally
            {
                host.Dispose();
            }
        }

        private static Process StartProcess(
            string executable, string directory, string arguments, Action<string> onOutput, Action<string> onError)
        {
            ProcessStartInfo start = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // A first run prints the SDK banner and telemetry notice otherwise.
            start.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
            start.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

            Process process = new Process { StartInfo = start };
            process.OutputDataReceived += (sender, line) =>
            {
                if (line.Data != null)
                {
                    onOutput(line.Data);
                }
            };
            process.ErrorDataReceived += (sender, line) =>
            {
                if (line.Data != null)
                {
                    onError(line.Data);
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
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // It has already exited.
            }
        }

        // A Unity started from the Dock or Hub on macOS does not see the shell's PATH, so the usual
        // install locations are tried before PATH.
        public static string ResolveDotnet(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            string executable = Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            string[] candidates =
            {
                string.IsNullOrEmpty(root) ? null : Path.Combine(root, executable),
                "/usr/local/share/dotnet/dotnet",
                "/opt/homebrew/bin/dotnet",
                "/usr/local/bin/dotnet",
                "/usr/share/dotnet/dotnet",
                "/usr/lib/dotnet/dotnet",
                Path.Combine(home, ".dotnet", executable),
                @"C:\Program Files\dotnet\dotnet.exe"
            };
            foreach (string candidate in candidates)
            {
                if (candidate != null && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return executable;
        }

        private static string Number(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static void Append(StringBuilder arguments, params string[] values)
        {
            foreach (string value in values)
            {
                if (arguments.Length > 0)
                {
                    arguments.Append(' ');
                }

                arguments.Append(Quote(value));
            }
        }

        // One command-line argument, quoted when it has spaces or quotes (the Windows and Mono rules).
        public static string Quote(string value)
        {
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            StringBuilder quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1).Append('"');
                }
                else
                {
                    quoted.Append('\\', backslashes).Append(c);
                }

                backslashes = 0;
            }

            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }
    }
}
