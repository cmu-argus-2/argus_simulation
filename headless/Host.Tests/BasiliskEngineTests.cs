using NUnit.Framework;

namespace Argus.Simulation.Host.Tests
{
    public sealed class BasiliskEngineTests
    {
        // The command's start time travels as exact integer nanoseconds. Its seconds value drifts:
        // 81923 x 0.1 s is 8192.300000000001 s, which is not a whole number of nanoseconds.
        [TestCase(0L, 0UL)]
        [TestCase(81_923L, 8_192_300_000_000UL)]
        [TestCase(10_000_000L, 1_000_000_000_000_000UL)]
        public void CommandTimeIsExactOnTheStepGridForLongRuns(long sequence, ulong expected)
        {
            Assert.That(BasiliskEngine.CommandTimeNanoseconds(sequence, 100_000_000), Is.EqualTo(expected));
        }
    }
}
