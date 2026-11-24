using System;
using System.Collections.Generic;
using System.IO;
using Argus.Simulation.Core;
using UnityEditor;
using UnityEngine;

namespace Argus.Simulation.Editor
{
    // The Host Launcher Inspector's spacecraft profile editor (HOST-9 profiles, D11 devices): the
    // profile ID, the body, and each device with its common fields and the keys its kind declares.
    // The fields come from Core's DeviceKinds registry, so a new or changed kind shows up here with
    // no change to this code. The profile is a file the run file names, or an object inline in the
    // run file; inline edits are run-file edits. Validity stays the host's call (--validate).
    internal sealed class SpacecraftProfileSection
    {
        private readonly List<bool> _expanded = new List<bool>();
        private int _newKind;

        public SpacecraftProfileSection(string path, RunFileJson.JsonObject profile, string loadError)
        {
            Path = path;
            Profile = profile;
            LoadError = loadError;
        }

        // The profile file, or null when the profile is inline in the run file.
        public string Path { get; private set; }
        public RunFileJson.JsonObject Profile { get; }
        public string LoadError { get; }
        public bool Dirty { get; private set; }

        // The other run files in runs/ that fly the same profile file, for the shared-file warning.
        public List<string> SharedWith { get; } = new List<string>();

        public static SpacecraftProfileSection LoadFile(string path)
        {
            try
            {
                RunFileJson.JsonObject profile = RunFileJson.Parse(File.ReadAllText(path)) as RunFileJson.JsonObject;
                return new SpacecraftProfileSection(path, profile, profile == null ? "The profile is not a JSON object." : null);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                exception is FormatException)
            {
                return new SpacecraftProfileSection(path, null, $"Cannot read the spacecraft profile {path}: {exception.Message}");
            }
        }

        public void Save()
        {
            File.WriteAllText(Path, Profile.Write());
            Dirty = false;
        }

        public void SaveAs(string path)
        {
            Path = path;
            string id = System.IO.Path.GetFileNameWithoutExtension(path);
            if (Profile["profileId"] is RunFileJson.Scalar profileId && IsPathSafeId(id))
            {
                profileId.StringValue = id;
            }

            Save();
        }

        // Draws the profile; returns true when an inline profile changed, which the run file owns.
        public bool Draw(double fixedStepSeconds)
        {
            bool changed = false;
            changed |= RunFileFields.Text("Profile ID", Profile, "profileId",
                "Recorded in each run's manifest: [a-z0-9][a-z0-9._-], at most 64 characters.");

            if (Profile["body"] is RunFileJson.JsonObject body)
            {
                EditorGUILayout.LabelField("Body", EditorStyles.boldLabel);
                changed |= RunFileFields.Number("Mass (kg)", body, "massKilograms");
                changed |= RunFileFields.Matrix3("Inertia (kg m², body axes)", body["inertiaBodyKilogramSquareMeters"]);
            }

            if (Profile["devices"] is RunFileJson.JsonArray devices)
            {
                EditorGUILayout.LabelField($"Devices ({devices.Items.Count})", EditorStyles.boldLabel);
                while (_expanded.Count < devices.Items.Count)
                {
                    _expanded.Add(false);
                }

                for (int i = 0; i < devices.Items.Count; i++)
                {
                    if (!(devices.Items[i] is RunFileJson.JsonObject device))
                    {
                        EditorGUILayout.LabelField($"devices[{i}]", "not an object; edit it in the file");
                        continue;
                    }

                    if (DrawDevice(devices, i, device, ref changed))
                    {
                        // Removed: the list changed under the loop.
                        break;
                    }
                }

                changed |= DrawAddDevice(devices, fixedStepSeconds);
            }
            else
            {
                EditorGUILayout.HelpBox("The profile has no devices list; edit it in the file.", MessageType.Warning);
            }

            if (changed)
            {
                Dirty = true;
            }

            return changed;
        }

        // Returns true when the device was removed.
        private bool DrawDevice(RunFileJson.JsonArray devices, int index, RunFileJson.JsonObject device, ref bool changed)
        {
            string id = (device["deviceId"] as RunFileJson.Scalar)?.StringValue ?? $"devices[{index}]";
            string kindName = (device["kind"] as RunFileJson.Scalar)?.StringValue ?? "?";
            IDeviceKind kind = DeviceKinds.Find(kindName);

            using (new EditorGUILayout.HorizontalScope())
            {
                _expanded[index] = EditorGUILayout.Foldout(_expanded[index], $"{id}   ({kindName})", true);
                if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(60.0f)) &&
                    EditorUtility.DisplayDialog("Remove device", $"Remove {id} from the profile?", "Remove", "Cancel"))
                {
                    devices.Items.RemoveAt(index);
                    _expanded.RemoveAt(index);
                    changed = true;
                    return true;
                }
            }

            if (!_expanded[index])
            {
                return false;
            }

            using (new EditorGUI.IndentLevelScope())
            {
                changed |= RunFileFields.Text("Device ID", device, "deviceId", "Unique in the profile; [a-z0-9][a-z0-9._-] to be recorded.");
                EditorGUILayout.LabelField("Kind", kind == null ? $"{kindName} (not a registered kind)" : $"{kindName} ({kind.Role})");
                changed |= RunFileFields.Text("Model Name", device, "modelName");
                changed |= RunFileFields.Text("Frame ID", device, "frameId");
                if (device["samplePeriodSeconds"] != null || kind?.Role == DeviceRole.Sensor)
                {
                    changed |= RunFileFields.Number("Sample Period (s)", device, "samplePeriodSeconds",
                        "A whole multiple of the run's fixed step.");
                }

                if (device["controllerVisible"] != null || kind?.Role == DeviceRole.Sensor)
                {
                    changed |= RunFileFields.Flag("Controller Visible", device, "controllerVisible",
                        "False for ground truth: the gateway never gives its frames to a controller.");
                }

                if (device["mount"] is RunFileJson.JsonObject mount)
                {
                    changed |= RunFileFields.Vector3("Position (m, body)", mount["positionBodyMeters"], "From the centre of mass, body axes.");
                    changed |= RunFileFields.Quaternion("Device to Body", mount["deviceToBody"],
                        kind?.Role == DeviceRole.Actuator ? "An actuator's must be the identity: its axis is a parameter." : null);
                }

                if (kind == null)
                {
                    EditorGUILayout.HelpBox($"'{kindName}' is not a registered kind ({DeviceKinds.Names}); edit its parameters in the file.", MessageType.Warning);
                    return false;
                }

                changed |= DrawKeys("Parameters", device, "parameters", kind.ParameterKeys);
                if (kind.InitialStateKeys.Count > 0)
                {
                    changed |= DrawKeys("Initial State", device, "initialState", kind.InitialStateKeys);
                }
            }

            return false;
        }

        // The kind's declared keys, each found flat ("gyroscope.range_rad_per_s") or in a group
        // object ("gyroscope": { "range_rad_per_s" }), as the run file allows either.
        private static bool DrawKeys(string label, RunFileJson.JsonObject device, string field, IReadOnlyList<DeviceKey> keys)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            if (!(device[field] is RunFileJson.JsonObject values))
            {
                if (GUILayout.Button($"Add {label}", EditorStyles.miniButton))
                {
                    device.Set(field, NewValues(keys));
                    return true;
                }

                return false;
            }

            bool changed = false;
            using (new EditorGUI.IndentLevelScope())
            {
                foreach (DeviceKey key in keys)
                {
                    RunFileJson node = Find(values, key.Name);
                    if (node == null)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(key.Name, "missing");
                            if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(40.0f)))
                            {
                                Put(values, key.Name, RunFileFields.DefaultValue(key.Type));
                                changed = true;
                            }
                        }

                        continue;
                    }

                    changed |= RunFileFields.Value(key.Name, node, key.Type);
                }
            }

            return changed;
        }

        private bool DrawAddDevice(RunFileJson.JsonArray devices, double fixedStepSeconds)
        {
            IReadOnlyList<IDeviceKind> kinds = DeviceKinds.All;
            string[] names = new string[kinds.Count];
            for (int i = 0; i < kinds.Count; i++)
            {
                names[i] = $"{kinds[i].Name} ({kinds[i].Role})";
            }

            bool added = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                _newKind = EditorGUILayout.Popup("Add Device", Mathf.Clamp(_newKind, 0, names.Length - 1), names);
                if (GUILayout.Button("Add", GUILayout.Width(60.0f)))
                {
                    devices.Items.Add(NewDevice(devices, kinds[_newKind], fixedStepSeconds));
                    _expanded.Add(true);
                    added = true;
                }
            }

            EditorGUILayout.LabelField(
                "A new device copies the profile's last device of that kind, with a new ID. A kind the profile has " +
                "none of starts from zeros, and Validate lists what to fill in.",
                EditorStyles.wordWrappedMiniLabel);
            return added;
        }

        // A copy of the profile's last device of this kind with a new ID, or one built from the
        // kind's declared keys.
        private static RunFileJson.JsonObject NewDevice(RunFileJson.JsonArray devices, IDeviceKind kind, double fixedStepSeconds)
        {
            string id = UniqueId(devices, kind.Name);
            RunFileJson.JsonObject template = null;
            foreach (RunFileJson item in devices.Items)
            {
                if (item is RunFileJson.JsonObject existing && (existing["kind"] as RunFileJson.Scalar)?.StringValue == kind.Name)
                {
                    template = existing;
                }
            }

            RunFileJson.JsonObject device;
            if (template != null)
            {
                device = (RunFileJson.JsonObject)template.Clone();
            }
            else
            {
                device = new RunFileJson.JsonObject();
                device.Set("deviceId", RunFileJson.String(id));
                device.Set("kind", RunFileJson.String(kind.Name));
                device.Set("modelName", RunFileJson.String("unspecified"));
                device.Set("frameId", RunFileJson.String(id));
                if (kind.Role == DeviceRole.Sensor)
                {
                    device.Set("samplePeriodSeconds", RunFileJson.Number(fixedStepSeconds > 0.0 ? fixedStepSeconds : 0.1));
                    device.Set("controllerVisible", RunFileJson.Flag(false));
                }

                RunFileJson.JsonObject mount = new RunFileJson.JsonObject();
                mount.Set("positionBodyMeters", RunFileFields.NumberArray(0.0, 0.0, 0.0));
                mount.Set("deviceToBody", RunFileFields.Identity());
                device.Set("mount", mount);
                device.Set("parameters", NewValues(kind.ParameterKeys));
                if (kind.InitialStateKeys.Count > 0)
                {
                    device.Set("initialState", NewValues(kind.InitialStateKeys));
                }
            }

            device.Set("deviceId", RunFileJson.String(id));
            device.Set("frameId", RunFileJson.String(id.Replace('.', '_')));
            return device;
        }

        private static RunFileJson.JsonObject NewValues(IReadOnlyList<DeviceKey> keys)
        {
            RunFileJson.JsonObject values = new RunFileJson.JsonObject();
            foreach (DeviceKey key in keys)
            {
                Put(values, key.Name, RunFileFields.DefaultValue(key.Type));
            }

            return values;
        }

        private static string UniqueId(RunFileJson.JsonArray devices, string kindName)
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (RunFileJson item in devices.Items)
            {
                if (item is RunFileJson.JsonObject device && device["deviceId"] is RunFileJson.Scalar id)
                {
                    taken.Add(id.StringValue);
                }
            }

            for (int n = 1; ; n++)
            {
                string candidate = $"{kindName}.{n}";
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        // The value of a dotted key, flat or in nested group objects; null when absent.
        private static RunFileJson Find(RunFileJson.JsonObject values, string key)
        {
            RunFileJson flat = values[key];
            if (flat != null)
            {
                return flat;
            }

            string[] parts = key.Split('.');
            RunFileJson.JsonObject current = values;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current = current[parts[i]] as RunFileJson.JsonObject;
                if (current == null)
                {
                    return null;
                }
            }

            return current[parts[parts.Length - 1]];
        }

        // Sets a dotted key in nested group objects, creating the groups it needs.
        private static void Put(RunFileJson.JsonObject values, string key, RunFileJson value)
        {
            string[] parts = key.Split('.');
            RunFileJson.JsonObject current = values;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!(current[parts[i]] is RunFileJson.JsonObject group))
                {
                    group = new RunFileJson.JsonObject();
                    current.Set(parts[i], group);
                }

                current = group;
            }

            current.Set(parts[parts.Length - 1], value);
        }

        private static bool IsPathSafeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64 || !(char.IsLower(id[0]) || char.IsDigit(id[0])))
            {
                return false;
            }

            foreach (char c in id)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
