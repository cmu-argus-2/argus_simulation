using System;
using System.IO;
using NUnit.Framework;

namespace Argus.Simulation.Host.Tests
{
    // --start-basilisk, --stop-on-stdin-eof (BasiliskServiceProcess) and --validate's coverage check
    // (KernelSetCoverage). Starting the real service is
    // CI's "Basilisk service and host run" job, which runs the host with --start-basilisk.
    public sealed class BasiliskServiceProcessTests
    {
        [Test]
        public void TheFlagsTakeNoValue()
        {
            Assert.That(HostOptions.TryParse(
                new[] { "--start-basilisk", "--duration-s", "5", "--stop-on-stdin-eof" }, out HostOptions options, out string error),
                Is.True, error);
            Assert.That(options.StartBasilisk, Is.True);
            Assert.That(options.StopOnStandardInputEnd, Is.True);
            Assert.That(options.DurationSeconds, Is.EqualTo(5.0));
            Assert.That(options.BasiliskDirectory, Is.Null);
        }

        [TestCase(new[] { "--replay", "run", "--start-basilisk" }, "--start-basilisk configures a live run")]
        [TestCase(new[] { "--basilisk-dir", "Argus.Basilisk" }, "--basilisk-dir needs --start-basilisk")]
        public void StartingTheServiceIsOnlyForALiveRun(string[] args, string expected)
        {
            Assert.That(HostOptions.TryParse(args, out _, out string error), Is.False);
            Assert.That(error, Does.StartWith(expected));
        }

        // argus-2026-11-02 covers 1962-01-20 to 2026-08-06T00:00:00Z.
        [TestCase("2026-06-01T12:00:00Z", 3600.0, null)]
        [TestCase("2026-08-05T23:00:00Z", 3600.0, null)]
        [TestCase("2026-08-05T23:30:00Z", 3600.0, "the run ends at 2026-08-06T00:30:00Z")]
        [TestCase("2026-09-01T00:00:00Z", 60.0, "epochUtc: 2026-09-01T00:00:00Z is outside")]
        public void ValidateChecksTheKernelSetCoversTheWholeRun(string epoch, double duration, string expected)
        {
            string problem = KernelSetCoverage.Problem(
                BasiliskServiceProcess.FindDirectory(), "argus-2026-11-02", DateTimeOffset.Parse(epoch), duration);
            Assert.That(problem, expected == null ? Is.Null : Does.StartWith(expected));
        }

        [Test]
        public void TheServiceDirectoryIsFoundAboveTheHost()
        {
            string directory = BasiliskServiceProcess.FindDirectory();
            Assert.That(directory, Is.Not.Null);
            Assert.That(File.Exists(Path.Combine(directory, "argus_basilisk", "__main__.py")), Is.True);
        }
    }
}
