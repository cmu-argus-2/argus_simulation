using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Argus.Simulation.Core
{
    // Loads files written by Argus.Spice/generate_reference.py. The header's format, frames,
    // and units are checked so a generator change cannot silently alter the meaning.
    public static class SpiceReferenceFile
    {
        public const string FormatName = "argus.spice.environment_reference";
        public const int FormatVersion = 1;

        public static TabulatedEphemerisProvider Load(string json)
        {
            if (!(JsonReader.Parse(json) is Dictionary<string, object> root))
            {
                throw new FormatException("SPICE reference root must be a JSON object.");
            }

            RequireEqual(GetString(root, "format"), FormatName, "format");
            RequireEqual(GetNumber(root, "format_version"), FormatVersion, "format_version");

            Dictionary<string, object> frames = GetObject(root, "frames");
            RequireEqual(GetString(frames, "inertial"), "J2000", "frames.inertial");
            RequireEqual(GetString(frames, "earth_fixed"), "ITRF93", "frames.earth_fixed");

            Dictionary<string, object> units = GetObject(root, "units");
            RequireEqual(GetString(units, "t"), "s", "units.t");
            RequireEqual(GetString(units, "rotation_rate"), "1/s", "units.rotation_rate");
            RequireEqual(GetString(units, "positions"), "m", "units.positions");

            Dictionary<string, object> sun = GetObject(root, "sun");
            RequireEqual(GetString(sun, "target"), "SUN", "sun.target");
            RequireEqual(GetString(sun, "observer"), "EARTH", "sun.observer");

            Dictionary<string, object> time = GetObject(root, "time");
            DateTimeOffset epochUtc = DateTimeOffset.Parse(
                GetString(time, "epoch_utc"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

            List<object> rows = GetArray(root, "samples");
            int expectedCount = (int)GetNumber(time, "sample_count");
            if (rows.Count != expectedCount)
            {
                throw new FormatException($"Expected {expectedCount} samples but found {rows.Count}.");
            }

            List<EphemerisSample> knots = new List<EphemerisSample>(rows.Count);
            for (int index = 0; index < rows.Count; index++)
            {
                if (!(rows[index] is Dictionary<string, object> row))
                {
                    throw new FormatException($"Sample {index} must be an object.");
                }

                double t = GetNumber(row, "t");
                knots.Add(new EphemerisSample(
                    t,
                    epochUtc.AddSeconds(t),
                    GetNumber(row, "et"),
                    Matrix3d.FromRowMajor(GetNumbers(row, "rotation", 9)),
                    Matrix3d.FromRowMajor(GetNumbers(row, "rotation_rate", 9)),
                    ToVector(GetNumbers(row, "sun_position_j2000_m", 3))));
            }

            string scenario = GetString(GetObject(root, "scenario"), "name");
            IEnumerable<string> kernels = GetArray(root, "kernels")
                .OfType<Dictionary<string, object>>()
                .Select(kernel => GetString(kernel, "file"));
            string sourceName = $"spice-reference:{scenario} [{string.Join(", ", kernels)}]";

            return new TabulatedEphemerisProvider(sourceName, epochUtc, knots);
        }

        private static Vector3d ToVector(double[] values) => new Vector3d(values[0], values[1], values[2]);

        private static Dictionary<string, object> GetObject(Dictionary<string, object> parent, string key) =>
            Get(parent, key) as Dictionary<string, object> ??
            throw new FormatException($"'{key}' must be an object.");

        private static List<object> GetArray(Dictionary<string, object> parent, string key) =>
            Get(parent, key) as List<object> ?? throw new FormatException($"'{key}' must be an array.");

        private static string GetString(Dictionary<string, object> parent, string key) =>
            Get(parent, key) as string ?? throw new FormatException($"'{key}' must be a string.");

        private static double GetNumber(Dictionary<string, object> parent, string key) =>
            Get(parent, key) is double value ? value : throw new FormatException($"'{key}' must be a number.");

        private static double[] GetNumbers(Dictionary<string, object> parent, string key, int count)
        {
            List<object> values = GetArray(parent, key);
            if (values.Count != count || values.Any(value => !(value is double)))
            {
                throw new FormatException($"'{key}' must contain {count} numbers.");
            }

            return values.Cast<double>().ToArray();
        }

        private static object Get(Dictionary<string, object> parent, string key) =>
            parent.TryGetValue(key, out object value) ? value : throw new FormatException($"Missing '{key}'.");

        private static void RequireEqual<T>(T actual, T expected, string field)
        {
            if (!EqualityComparer<T>.Default.Equals(actual, expected))
            {
                throw new FormatException($"Unsupported {field}: expected '{expected}', found '{actual}'.");
            }
        }
    }
}
