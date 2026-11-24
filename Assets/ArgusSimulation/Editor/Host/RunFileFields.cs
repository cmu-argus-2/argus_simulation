using Argus.Simulation.Core;
using UnityEditor;
using UnityEngine;

namespace Argus.Simulation.Editor
{
    // Inspector fields over RunFileJson values: each draws the value in place and returns true when
    // the user changed it. A value of the wrong JSON shape is shown as a note, left for --validate
    // to report, and never rewritten. Delayed fields apply on Enter or when focus leaves.
    internal static class RunFileFields
    {
        public static bool Text(string label, RunFileJson.JsonObject parent, string key, string tooltip = null) =>
            Text(label, parent[key], tooltip);

        public static bool Text(string label, RunFileJson node, string tooltip = null)
        {
            if (!(node is RunFileJson.Scalar scalar) || !scalar.IsString)
            {
                return WrongShape(label, node, "text");
            }

            string value = scalar.StringValue;
            string edited = EditorGUILayout.DelayedTextField(new GUIContent(label, tooltip), value);
            if (edited == value)
            {
                return false;
            }

            scalar.StringValue = edited;
            return true;
        }

        public static bool Number(string label, RunFileJson.JsonObject parent, string key, string tooltip = null) =>
            Number(label, parent[key], tooltip);

        public static bool Number(string label, RunFileJson node, string tooltip = null)
        {
            if (!(node is RunFileJson.Scalar scalar) || double.IsNaN(scalar.NumberValue))
            {
                return WrongShape(label, node, "a number");
            }

            double value = scalar.NumberValue;
            double edited = EditorGUILayout.DelayedDoubleField(new GUIContent(label, tooltip), value);
            if (edited.Equals(value))
            {
                return false;
            }

            scalar.NumberValue = edited;
            return true;
        }

        public static bool Integer(string label, RunFileJson.JsonObject parent, string key, string tooltip = null)
        {
            if (!(parent[key] is RunFileJson.Scalar scalar) || !scalar.IsInteger)
            {
                return WrongShape(label, parent[key], "a whole number");
            }

            long value = scalar.IntegerValue;
            long edited = EditorGUILayout.LongField(new GUIContent(label, tooltip), value);
            if (edited == value)
            {
                return false;
            }

            scalar.IntegerValue = edited;
            return true;
        }

        public static bool Flag(string label, RunFileJson.JsonObject parent, string key, string tooltip = null) =>
            Flag(label, parent[key], tooltip);

        public static bool Flag(string label, RunFileJson node, string tooltip = null)
        {
            if (!(node is RunFileJson.Scalar scalar) || !scalar.IsFlag)
            {
                return WrongShape(label, node, "true or false");
            }

            bool value = scalar.FlagValue;
            bool edited = EditorGUILayout.Toggle(new GUIContent(label, tooltip), value);
            if (edited == value)
            {
                return false;
            }

            scalar.FlagValue = edited;
            return true;
        }

        // [x, y, z] on one row.
        public static bool Vector3(string label, RunFileJson node, string tooltip = null)
        {
            if (!(node is RunFileJson.JsonArray array) || array.Items.Count != 3 || !AllNumbers(array))
            {
                return WrongShape(label, node, "[x, y, z]");
            }

            return Row(label, tooltip, new[] { "x", "y", "z" }, new[]
            {
                (RunFileJson.Scalar)array.Items[0], (RunFileJson.Scalar)array.Items[1], (RunFileJson.Scalar)array.Items[2]
            });
        }

        // { "x", "y", "z", "w" } on one row.
        public static bool Quaternion(string label, RunFileJson node, string tooltip = null)
        {
            string[] axes = { "x", "y", "z", "w" };
            RunFileJson.Scalar[] values = new RunFileJson.Scalar[4];
            RunFileJson.JsonObject quaternion = node as RunFileJson.JsonObject;
            for (int i = 0; i < 4; i++)
            {
                values[i] = quaternion?[axes[i]] as RunFileJson.Scalar;
                if (values[i] == null || double.IsNaN(values[i].NumberValue))
                {
                    return WrongShape(label, node, "{ x, y, z, w }");
                }
            }

            return Row(label, tooltip, axes, values);
        }

        // Three rows [[..], [..], [..]].
        public static bool Matrix3(string label, RunFileJson node, string tooltip = null)
        {
            if (!(node is RunFileJson.JsonArray rows) || rows.Items.Count != 3)
            {
                return WrongShape(label, node, "three rows of three numbers");
            }

            foreach (RunFileJson row in rows.Items)
            {
                if (!(row is RunFileJson.JsonArray cells) || cells.Items.Count != 3 || !AllNumbers(cells))
                {
                    return WrongShape(label, node, "three rows of three numbers");
                }
            }

            EditorGUILayout.LabelField(new GUIContent(label, tooltip));
            bool changed = false;
            using (new EditorGUI.IndentLevelScope())
            {
                for (int r = 0; r < 3; r++)
                {
                    RunFileJson.JsonArray cells = (RunFileJson.JsonArray)rows.Items[r];
                    changed |= Row($"row {r + 1}", null, new[] { "", "", "" }, new[]
                    {
                        (RunFileJson.Scalar)cells.Items[0], (RunFileJson.Scalar)cells.Items[1], (RunFileJson.Scalar)cells.Items[2]
                    });
                }
            }

            return changed;
        }

        // One field of a device kind's declared type.
        public static bool Value(string label, RunFileJson node, DeviceValueType type)
        {
            switch (type)
            {
                case DeviceValueType.Number: return Number(label, node);
                case DeviceValueType.Vector3: return Vector3(label, node);
                case DeviceValueType.Quaternion: return Quaternion(label, node);
                case DeviceValueType.Matrix3: return Matrix3(label, node);
                case DeviceValueType.Flag: return Flag(label, node);
                case DeviceValueType.Text: return Text(label, node);
                default: return WrongShape(label, node, type.ToString());
            }
        }

        // A value of a declared type for a new device: zero, the identity rotation, false or "".
        public static RunFileJson DefaultValue(DeviceValueType type)
        {
            switch (type)
            {
                case DeviceValueType.Vector3: return NumberArray(0.0, 0.0, 0.0);
                case DeviceValueType.Quaternion: return Identity();
                case DeviceValueType.Matrix3:
                    RunFileJson.JsonArray rows = new RunFileJson.JsonArray();
                    rows.Items.Add(NumberArray(0.0, 0.0, 0.0));
                    rows.Items.Add(NumberArray(0.0, 0.0, 0.0));
                    rows.Items.Add(NumberArray(0.0, 0.0, 0.0));
                    return rows;
                case DeviceValueType.Flag: return RunFileJson.Flag(false);
                case DeviceValueType.Text: return RunFileJson.String("");
                default: return RunFileJson.Number(0.0);
            }
        }

        public static RunFileJson.JsonArray NumberArray(params double[] values)
        {
            RunFileJson.JsonArray array = new RunFileJson.JsonArray { OneLine = true };
            foreach (double value in values)
            {
                array.Items.Add(RunFileJson.Number(value));
            }

            return array;
        }

        public static RunFileJson.JsonObject Identity()
        {
            RunFileJson.JsonObject quaternion = new RunFileJson.JsonObject { OneLine = true };
            quaternion.Set("x", RunFileJson.Number(0.0));
            quaternion.Set("y", RunFileJson.Number(0.0));
            quaternion.Set("z", RunFileJson.Number(0.0));
            quaternion.Set("w", RunFileJson.Number(1.0));
            return quaternion;
        }

        private static bool Row(string label, string tooltip, string[] names, RunFileJson.Scalar[] values)
        {
            Rect rect = EditorGUILayout.GetControlRect();
            rect = EditorGUI.PrefixLabel(rect, new GUIContent(label, tooltip));
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = names[0].Length > 0 ? 12.0f : 0.001f;
            float width = rect.width / values.Length;
            bool changed = false;
            for (int i = 0; i < values.Length; i++)
            {
                Rect cell = new Rect(rect.x + i * width, rect.y, width - 2.0f, rect.height);
                double value = values[i].NumberValue;
                double edited = EditorGUI.DelayedDoubleField(cell, names[i], value);
                if (!edited.Equals(value))
                {
                    values[i].NumberValue = edited;
                    changed = true;
                }
            }

            EditorGUIUtility.labelWidth = labelWidth;
            EditorGUI.indentLevel = indent;
            return changed;
        }

        private static bool AllNumbers(RunFileJson.JsonArray array) =>
            array.Items.TrueForAll(item => item is RunFileJson.Scalar scalar && !double.IsNaN(scalar.NumberValue));

        private static bool WrongShape(string label, RunFileJson node, string expected)
        {
            EditorGUILayout.LabelField(label, node == null ? "missing (Validate reports it)" : $"not {expected}; edit it in the file");
            return false;
        }
    }
}
