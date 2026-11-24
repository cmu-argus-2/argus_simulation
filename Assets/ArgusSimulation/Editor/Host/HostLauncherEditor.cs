using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Argus.Simulation.Unity;
using UnityEditor;
using UnityEngine;

namespace Argus.Simulation.Editor
{
    // The Host Launcher's Inspector: the launch settings, then the run file, chosen from
    // headless/Host/runs/ and editable in place, then the spacecraft profile it flies
    // (SpacecraftProfileSection). The run file's scenario fields are the epoch, step, seed, kernel
    // set, orbit, initial attitude and rate, and the profile path. Save writes back only what changed
    // (RunFileJson), Save As starts another run file or profile, and Validate asks the host itself
    // (--validate) about the run file and profile as shown, saved or not: the run's own parser plus
    // the kernel set's coverage (headless/README.md, Run configuration files).
    [CustomEditor(typeof(HostLauncher))]
    internal sealed class HostLauncherEditor : UnityEditor.Editor
    {
        private SerializedProperty _runConfiguration;
        private SerializedProperty _replayDirectory;
        private SerializedProperty _durationSeconds;
        private SerializedProperty _dotnetExecutable;

        private string _loadedPath;
        private RunFileJson.JsonObject _runFile;
        private string _loadError;
        private bool _dirty;
        private bool _showRunFile = true;
        private bool _showProfile = true;
        private SpacecraftProfileSection _profile;
        private string _profileLoadedFor;

        private Task<ValidationResult> _validation;
        private ValidationResult _lastValidation;

        private void OnEnable()
        {
            _runConfiguration = serializedObject.FindProperty("runConfiguration");
            _replayDirectory = serializedObject.FindProperty("replayDirectory");
            _durationSeconds = serializedObject.FindProperty("durationSeconds");
            _dotnetExecutable = serializedObject.FindProperty("dotnetExecutable");
            EditorApplication.update += RepaintWhenValidated;
        }

        private void OnDisable()
        {
            EditorApplication.update -= RepaintWhenValidated;
            if (_dirty && _runFile != null && EditorUtility.DisplayDialog(
                "Unsaved run file",
                $"Save your changes to {_loadedPath}?",
                "Save",
                "Discard"))
            {
                Save();
            }

            if (ProfileFileDirty && EditorUtility.DisplayDialog(
                "Unsaved spacecraft profile",
                $"Save your changes to {_profile.Path}?",
                "Save",
                "Discard"))
            {
                SaveProfile();
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            // The run file is chosen in the Run File section below.
            DrawPropertiesExcluding(serializedObject, "runConfiguration");
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            _showRunFile = EditorGUILayout.Foldout(_showRunFile, "Run File", true, EditorStyles.foldoutHeader);
            if (!_showRunFile)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_replayDirectory.stringValue))
            {
                EditorGUILayout.HelpBox("Replay Directory is set, so Play replays that recording and the run file is unused.", MessageType.Info);
                return;
            }

            DrawRunFileChooser();
            string path = RunFilePath();
            if (path != _loadedPath)
            {
                Load(path);
            }

            if (_loadError != null)
            {
                EditorGUILayout.HelpBox(_loadError, MessageType.Error);
                if (GUILayout.Button("Reload"))
                {
                    Load(path);
                }

                return;
            }

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                DrawRunFields();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_dirty))
                {
                    if (GUILayout.Button("Save"))
                    {
                        Save();
                    }

                    if (GUILayout.Button("Revert"))
                    {
                        Load(path);
                    }
                }

                if (GUILayout.Button("Save As..."))
                {
                    SaveAs();
                }

                if (GUILayout.Button("Open File"))
                {
                    EditorUtility.OpenWithDefaultApp(path);
                }
            }

            DrawProfile();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_validation != null))
            {
                if (GUILayout.Button(_validation != null ? "Validating..." : "Validate Run File and Spacecraft"))
                {
                    StartValidation();
                }
            }

            if (_dirty || ProfileFileDirty)
            {
                EditorGUILayout.HelpBox("Unsaved changes: Play runs the saved files, so Save first.", MessageType.Warning);
            }

            if (_lastValidation != null)
            {
                EditorGUILayout.HelpBox(_lastValidation.Message, _lastValidation.Valid ? MessageType.Info : MessageType.Error);
            }
        }

        private bool ProfileFileDirty => _profile != null && _profile.Path != null && _profile.Dirty;

        private void DrawProfile()
        {
            string profilePath = ProfilePath();
            string key = profilePath ?? "inline";
            if (key != _profileLoadedFor)
            {
                LoadProfile();
            }

            EditorGUILayout.Space();
            _showProfile = EditorGUILayout.Foldout(_showProfile, "Spacecraft Profile", true, EditorStyles.foldoutHeader);
            if (!_showProfile || _profile == null)
            {
                return;
            }

            if (_profile.LoadError != null)
            {
                EditorGUILayout.HelpBox(_profile.LoadError, MessageType.Error);
                if (GUILayout.Button("Reload Profile"))
                {
                    LoadProfile();
                }

                return;
            }

            EditorGUILayout.LabelField("File", _profile.Path == null ? "inline in the run file" : Relative(_profile.Path),
                EditorStyles.wordWrappedMiniLabel);
            if (_profile.SharedWith.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Also flown by {string.Join(", ", _profile.SharedWith)}: saving changes them too. Save Profile As makes your own.",
                    MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                double fixedStep = (_runFile["fixedStepSeconds"] as RunFileJson.Scalar)?.NumberValue ?? 0.1;
                if (_profile.Draw(fixedStep) && _profile.Path == null)
                {
                    _dirty = true;
                }
            }

            if (_profile.Path == null)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!_profile.Dirty))
                {
                    if (GUILayout.Button("Save Profile"))
                    {
                        SaveProfile();
                    }

                    if (GUILayout.Button("Revert Profile"))
                    {
                        LoadProfile();
                    }
                }

                if (GUILayout.Button("Save Profile As..."))
                {
                    SaveProfileAs();
                }

                if (GUILayout.Button("Open File"))
                {
                    EditorUtility.OpenWithDefaultApp(_profile.Path);
                }
            }
        }

        private void LoadProfile()
        {
            string profilePath = ProfilePath();
            _profileLoadedFor = profilePath ?? "inline";
            _lastValidation = null;
            if (profilePath != null)
            {
                _profile = SpacecraftProfileSection.LoadFile(profilePath);
                foreach (string runFile in RunFilesFlying(profilePath))
                {
                    if (!string.Equals(runFile, _loadedPath, StringComparison.Ordinal))
                    {
                        _profile.SharedWith.Add(System.IO.Path.GetFileName(runFile));
                    }
                }
            }
            else
            {
                _profile = _runFile["spacecraftProfile"] is RunFileJson.JsonObject inline
                    ? new SpacecraftProfileSection(null, inline, null)
                    : null;
            }
        }

        // The run files in runs/ whose spacecraftProfile is this file.
        private static IEnumerable<string> RunFilesFlying(string profilePath)
        {
            if (!Directory.Exists(RunsDirectory))
            {
                yield break;
            }

            foreach (string runFile in Directory.GetFiles(RunsDirectory, "*.json"))
            {
                string named = null;
                try
                {
                    if (RunFileJson.Parse(File.ReadAllText(runFile)) is RunFileJson.JsonObject run &&
                        run["spacecraftProfile"] is RunFileJson.Scalar profile && profile.IsString)
                    {
                        named = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(runFile) ?? "", profile.StringValue));
                    }
                }
                catch (Exception exception) when (exception is IOException || exception is FormatException)
                {
                }

                if (string.Equals(named, profilePath, StringComparison.Ordinal))
                {
                    yield return runFile;
                }
            }
        }

        private void SaveProfile()
        {
            try
            {
                _profile.Save();
                _lastValidation = null;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                _lastValidation = new ValidationResult(false, $"Could not save {Relative(_profile.Path)}: {exception.Message}");
            }
        }

        // Saves the profile as a new file (its ID becomes the file name) and points the run file at
        // it; the run file then has an unsaved change, so the shared profile is left as it was.
        private void SaveProfileAs()
        {
            string directory = Path.GetDirectoryName(_profile.Path) ?? HostLauncher.RepositoryRoot;
            string target = EditorUtility.SaveFilePanel(
                "Save spacecraft profile as", directory, Path.GetFileNameWithoutExtension(_profile.Path) + "-copy", "json");
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            target = Path.GetFullPath(target);
            try
            {
                _profile.SaveAs(target);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                _lastValidation = new ValidationResult(false, $"Could not save {target}: {exception.Message}");
                return;
            }

            ((RunFileJson.Scalar)_runFile["spacecraftProfile"]).StringValue =
                Path.GetRelativePath(Path.GetDirectoryName(_loadedPath) ?? "", target).Replace('\\', '/');
            _dirty = true;
            LoadProfile();
            _lastValidation = new ValidationResult(true,
                $"Saved {Relative(target)} and pointed this run file at it. Save the run file to fly it.");
        }

        private void DrawRunFields()
        {
            EditorGUILayout.LabelField("File", Relative(_loadedPath), EditorStyles.wordWrappedMiniLabel);
            StringField("Epoch UTC", _runFile, "epochUtc", "A UTC time ending in Z, inside the kernel set's coverage.");
            NumberField("Fixed Step (s)", _runFile, "fixedStepSeconds", "Every sensor's sample period must be a whole multiple of it.");
            IntegerField("Random Seed", _runFile, "randomSeed", "Seeds every sensor's noise; the same seed gives the same run.");
            StringField("Kernel Set", _runFile, "kernelSetId", "Argus.Basilisk/kernel_sets/<id>.json.");

            if (_runFile["initialOrbit"] is RunFileJson.JsonObject orbit)
            {
                EditorGUILayout.LabelField("Initial Orbit (osculating, J2000 axes)", EditorStyles.boldLabel);
                NumberField("Semi-major Axis (m)", orbit, "semiMajorAxisMeters", null);
                NumberField("Eccentricity", orbit, "eccentricity", null);
                NumberField("Inclination (deg)", orbit, "inclinationDegrees", null);
                NumberField("RAAN (deg)", orbit, "raanDegrees", "Measured from J2000 x, not from Greenwich.");
                NumberField("Arg. of Periapsis (deg)", orbit, "argumentOfPeriapsisDegrees", null);
                NumberField("True Anomaly (deg)", orbit, "trueAnomalyDegrees", null);
            }

            if (_runFile["initialAttitude"] is RunFileJson.JsonObject attitude)
            {
                EditorGUILayout.LabelField("Initial Attitude", EditorStyles.boldLabel);
                if (attitude["bodyToJ2000"] is RunFileJson.JsonObject quaternion)
                {
                    EditorGUILayout.LabelField("Body to J2000 (unit quaternion)");
                    using (new EditorGUI.IndentLevelScope())
                    {
                        foreach (string axis in new[] { "x", "y", "z", "w" })
                        {
                            NumberField(axis, quaternion, axis, null);
                        }
                    }
                }

                if (attitude["angularVelocityBodyRadiansPerSecond"] is RunFileJson.JsonArray rate && rate.Items.Count == 3)
                {
                    EditorGUILayout.LabelField("Body Rate (rad/s, body axes)");
                    using (new EditorGUI.IndentLevelScope())
                    {
                        string[] axes = { "x", "y", "z" };
                        for (int i = 0; i < 3; i++)
                        {
                            if (rate.Items[i] is RunFileJson.Scalar value)
                            {
                                NumberField(axes[i], value, null);
                            }
                        }
                    }
                }
            }

            EditorGUILayout.LabelField("Spacecraft", EditorStyles.boldLabel);
            if (_runFile["spacecraftProfile"] is RunFileJson.Scalar profile && profile.IsString)
            {
                StringField("Profile File", _runFile, "spacecraftProfile",
                    "Relative to the run file. Its body and devices are under Spacecraft Profile below.");
            }
            else
            {
                EditorGUILayout.LabelField("Profile", "inline in the run file (Spacecraft Profile below)");
            }
        }

        private void StringField(string label, RunFileJson.JsonObject parent, string key, string tooltip)
        {
            if (!(parent[key] is RunFileJson.Scalar scalar) || !scalar.IsString)
            {
                return;
            }

            string value = scalar.StringValue;
            string edited = EditorGUILayout.DelayedTextField(new GUIContent(label, tooltip), value);
            if (edited != value)
            {
                scalar.StringValue = edited;
                _dirty = true;
            }
        }

        private void NumberField(string label, RunFileJson.JsonObject parent, string key, string tooltip)
        {
            if (parent[key] is RunFileJson.Scalar scalar)
            {
                NumberField(label, scalar, tooltip);
            }
        }

        private void NumberField(string label, RunFileJson.Scalar scalar, string tooltip)
        {
            double value = scalar.NumberValue;
            double edited = EditorGUILayout.DelayedDoubleField(new GUIContent(label, tooltip), value);
            if (!edited.Equals(value))
            {
                scalar.NumberValue = edited;
                _dirty = true;
            }
        }

        private void IntegerField(string label, RunFileJson.JsonObject parent, string key, string tooltip)
        {
            if (!(parent[key] is RunFileJson.Scalar scalar))
            {
                return;
            }

            long value = scalar.IntegerValue;
            long edited = EditorGUILayout.LongField(new GUIContent(label, tooltip), value);
            if (edited != value)
            {
                scalar.IntegerValue = edited;
                _dirty = true;
            }
        }

        private static string RunsDirectory => Path.Combine(HostLauncher.RepositoryRoot, "headless", "Host", "runs");

        // A dropdown of the run files in headless/Host/runs/, plus the current one when it is
        // elsewhere. The frozen reference cases in runs/reference/ are not listed.
        private void DrawRunFileChooser()
        {
            string current = _runConfiguration.stringValue ?? "";
            List<string> files = new List<string>();
            if (Directory.Exists(RunsDirectory))
            {
                foreach (string file in Directory.GetFiles(RunsDirectory, "*.json"))
                {
                    if (!Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal))
                    {
                        files.Add(RepositoryPath(file));
                    }
                }
            }

            files.Sort(StringComparer.Ordinal);
            if (!files.Contains(current))
            {
                files.Add(current);
            }

            int index = files.IndexOf(current);
            string[] labels = files.ConvertAll(file => file.StartsWith("headless/Host/runs/", StringComparison.Ordinal)
                ? file.Substring("headless/Host/runs/".Length)
                : file).ToArray();
            int chosen = EditorGUILayout.Popup(
                new GUIContent("Run Configuration", "A run file in headless/Host/runs/. Save As starts a new one."), index, labels);
            if (chosen != index && ConfirmLeavingEdits())
            {
                _runConfiguration.stringValue = files[chosen];
                serializedObject.ApplyModifiedProperties();
            }
        }

        // True when the current file has no unsaved edits, or they were saved or discarded.
        private bool ConfirmLeavingEdits()
        {
            if (!_dirty)
            {
                return true;
            }

            int choice = EditorUtility.DisplayDialogComplex(
                "Unsaved run file", $"Save your changes to {Relative(_loadedPath)}?", "Save", "Cancel", "Discard");
            if (choice == 0)
            {
                Save();
            }

            return choice != 1;
        }

        // Writes the fields shown to a new run file, by default beside this one, and makes the
        // launcher use it. A relative spacecraft profile path is rewritten for the new folder.
        private void SaveAs()
        {
            string directory = Path.GetDirectoryName(_loadedPath) ?? RunsDirectory;
            string target = EditorUtility.SaveFilePanel(
                "Save run file as", directory, Path.GetFileNameWithoutExtension(_loadedPath) + "-copy", "json");
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            target = Path.GetFullPath(target);
            string profile = ProfilePath();
            if (profile != null && !Path.IsPathRooted(((RunFileJson.Scalar)_runFile["spacecraftProfile"]).StringValue))
            {
                ((RunFileJson.Scalar)_runFile["spacecraftProfile"]).StringValue =
                    Path.GetRelativePath(Path.GetDirectoryName(target) ?? "", profile).Replace('\\', '/');
            }

            _loadedPath = target;
            Save();
            _runConfiguration.stringValue = RepositoryPath(target);
            serializedObject.ApplyModifiedProperties();
            Load(target);
        }

        // Relative to the repository root with forward slashes when inside it; otherwise absolute.
        private static string RepositoryPath(string path)
        {
            string relative = Relative(Path.GetFullPath(path));
            return Path.IsPathRooted(relative) ? relative : relative.Replace('\\', '/');
        }

        private string RunFilePath() =>
            Path.GetFullPath(Path.Combine(HostLauncher.RepositoryRoot, _runConfiguration.stringValue ?? ""));

        // The profile file the run file names, or null when it is inline or unset.
        private string ProfilePath()
        {
            if (_runFile?["spacecraftProfile"] is RunFileJson.Scalar profile && profile.IsString &&
                !string.IsNullOrWhiteSpace(profile.StringValue))
            {
                return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_loadedPath) ?? "", profile.StringValue));
            }

            return null;
        }

        private void Load(string path)
        {
            _loadedPath = path;
            _profile = null;
            _profileLoadedFor = null;
            _runFile = null;
            _dirty = false;
            _lastValidation = null;
            try
            {
                _runFile = RunFileJson.Parse(File.ReadAllText(path)) as RunFileJson.JsonObject;
                _loadError = _runFile == null ? $"{Relative(path)} is not a JSON object." : null;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                exception is FormatException)
            {
                _loadError = $"Cannot read the run file {Relative(path)}: {exception.Message}";
            }
        }

        private void Save()
        {
            try
            {
                File.WriteAllText(_loadedPath, _runFile.Write());
                _dirty = false;
                _lastValidation = null;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                _lastValidation = new ValidationResult(false, $"Could not save {Relative(_loadedPath)}: {exception.Message}");
            }
        }

        // Validates what the Inspector shows: unsaved edits go to a temporary file beside the run
        // file, so a relative profile path still resolves, and that file is deleted afterwards.
        private void StartValidation()
        {
            string root = HostLauncher.RepositoryRoot;
            string dotnet = HostLauncher.ResolveDotnet(_dotnetExecutable.stringValue);
            string runFile = _loadedPath;
            // Unsaved edits are checked from temporary files beside the originals, so relative paths
            // still resolve. They are written after the build, which copies the runs and spacecraft
            // folders' JSON files next to the host, and deleted afterwards.
            string profileFile = ProfileFileDirty ? _profile.Path : null;
            string unsavedProfile = profileFile != null ? Unsaved(profileFile) : null;
            string unsavedProfileText = profileFile != null ? _profile.Profile.Write() : null;
            string unsavedText = null;
            if (_dirty || profileFile != null)
            {
                RunFileJson.JsonObject run = (RunFileJson.JsonObject)_runFile.Clone();
                if (unsavedProfile != null)
                {
                    run.Set("spacecraftProfile", RunFileJson.String(
                        Path.GetRelativePath(Path.GetDirectoryName(runFile) ?? "", unsavedProfile).Replace('\\', '/')));
                }

                unsavedText = run.Write();
            }

            string checkedFile = unsavedText != null ? Unsaved(runFile) : runFile;

            string duration = _durationSeconds.floatValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string arguments = $"{HostLauncher.Quote(Path.Combine(root, HostLauncher.HostAssembly))} --validate " +
                $"--run-config {HostLauncher.Quote(checkedFile)} --duration-s {duration}";
            _lastValidation = null;
            _validation = Task.Run(() =>
            {
                try
                {
                    StringBuilder build = new StringBuilder();
                    int built = HostLauncher.RunToEnd(dotnet, root, HostLauncher.BuildArguments, build, CancellationToken.None);
                    if (built != 0)
                    {
                        return new ValidationResult(false, $"dotnet build failed with code {built}:\n{build}");
                    }

                    if (unsavedText != null)
                    {
                        File.WriteAllText(checkedFile, unsavedText);
                    }

                    if (unsavedProfileText != null)
                    {
                        File.WriteAllText(unsavedProfile, unsavedProfileText);
                    }

                    StringBuilder output = new StringBuilder();
                    int code = HostLauncher.RunToEnd(dotnet, root, arguments, output, CancellationToken.None);
                    string message = output.ToString().Trim().Replace(checkedFile, runFile);
                    if (unsavedProfile != null)
                    {
                        message = message.Replace(unsavedProfile, profileFile);
                    }

                    return new ValidationResult(code == 0, code == 0 || code == 2 ? message : $"The host exited with code {code}:\n{message}");
                }
                catch (Exception exception)
                {
                    return new ValidationResult(false,
                        $"Could not run the host with {dotnet} ({exception.Message}). Install the .NET 8 SDK, or set Dotnet Executable.");
                }
                finally
                {
                    if (unsavedText != null && File.Exists(checkedFile))
                    {
                        File.Delete(checkedFile);
                    }

                    if (unsavedProfile != null && File.Exists(unsavedProfile))
                    {
                        File.Delete(unsavedProfile);
                    }
                }
            });
        }

        private static string Unsaved(string path) =>
            Path.Combine(Path.GetDirectoryName(path) ?? "", "." + Path.GetFileNameWithoutExtension(path) + ".unsaved.json");

        private void RepaintWhenValidated()
        {
            if (_validation != null && _validation.IsCompleted)
            {
                _lastValidation = _validation.Result;
                _validation = null;
                Repaint();
            }
        }

        private static string Relative(string path)
        {
            string root = HostLauncher.RepositoryRoot + Path.DirectorySeparatorChar;
            return path != null && path.StartsWith(root, StringComparison.Ordinal) ? path.Substring(root.Length) : path;
        }

        private sealed class ValidationResult
        {
            public ValidationResult(bool valid, string message)
            {
                Valid = valid;
                Message = message;
            }

            public bool Valid { get; }
            public string Message { get; }
        }
    }
}
