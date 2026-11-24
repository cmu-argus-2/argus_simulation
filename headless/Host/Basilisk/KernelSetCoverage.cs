using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Argus.Simulation.Host
{
    // Whether a kernel set covers a run, read from its manifest
    // (Argus.Basilisk/kernel_sets/<id>.json, coverage_utc, inclusive). It is the check the service
    // makes with OUT_OF_RANGE on the epoch and on every step (Argus.Basilisk/README.md §8), made
    // before anything starts, for --validate.
    internal static class KernelSetCoverage
    {
        // Null when the set covers epochUtc to epochUtc + durationSeconds; otherwise the problem.
        public static string Problem(string serviceDirectory, string kernelSetId, DateTimeOffset epochUtc, double durationSeconds)
        {
            if (serviceDirectory == null)
            {
                return $"kernelSetId: cannot check '{kernelSetId}': found no {BasiliskServiceProcess.DirectoryName} directory; pass --basilisk-dir";
            }

            string manifest = Path.Combine(serviceDirectory, "kernel_sets", kernelSetId + ".json");
            if (!File.Exists(manifest))
            {
                return $"kernelSetId: '{kernelSetId}' has no manifest {manifest}";
            }

            DateTimeOffset start;
            DateTimeOffset end;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
                JsonElement coverage = document.RootElement.GetProperty("coverage_utc");
                start = Parse(coverage.GetProperty("start").GetString());
                end = Parse(coverage.GetProperty("end").GetString());
            }
            catch (Exception exception) when (exception is JsonException || exception is FormatException ||
                exception is InvalidOperationException || exception is System.Collections.Generic.KeyNotFoundException)
            {
                return $"kernelSetId: cannot read coverage_utc from {manifest}: {exception.Message}";
            }

            DateTimeOffset runEnd = epochUtc.AddSeconds(durationSeconds);
            if (epochUtc < start || epochUtc > end)
            {
                return $"epochUtc: {Format(epochUtc)} is outside kernel set {kernelSetId}'s coverage, {Format(start)} to {Format(end)}";
            }

            if (runEnd > end)
            {
                return $"the run ends at {Format(runEnd)} (epochUtc + --duration-s), after kernel set {kernelSetId}'s coverage ends at " +
                    $"{Format(end)}; start earlier or run shorter";
            }

            return null;
        }

        private static DateTimeOffset Parse(string value) =>
            DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        private static string Format(DateTimeOffset value) =>
            value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
