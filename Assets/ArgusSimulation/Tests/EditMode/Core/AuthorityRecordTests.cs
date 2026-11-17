using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class AuthorityRecordTests
    {
        // CORE-9: a refusal or a loss says why; a claim or a release does not.
        [TestCase(AuthorityChange.Refused)]
        [TestCase(AuthorityChange.Lost)]
        public void ARefusalOrALossNeedsAReason(AuthorityChange change)
        {
            Assert.That(new AuthorityRecord("run", 2, 0.2, "fsw", change, "why").Reason, Is.EqualTo("why"));
            Assert.Throws<ArgumentException>(() => new AuthorityRecord("run", 2, 0.2, "fsw", change));
        }

        [TestCase(AuthorityChange.Claimed)]
        [TestCase(AuthorityChange.Released)]
        public void AClaimOrAReleaseHasNoReason(AuthorityChange change)
        {
            Assert.That(new AuthorityRecord("run", 2, 0.2, "fsw", change).Reason, Is.Null);
            Assert.Throws<ArgumentException>(() => new AuthorityRecord("run", 2, 0.2, "fsw", change, "why"));
        }

        [Test]
        public void ARecordNeedsARunAControllerAndAStep()
        {
            Assert.Throws<ArgumentException>(() => new AuthorityRecord(" ", 2, 0.2, "fsw", AuthorityChange.Claimed));
            Assert.Throws<ArgumentException>(() => new AuthorityRecord("run", 2, 0.2, "", AuthorityChange.Claimed));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AuthorityRecord("run", -1, 0.2, "fsw", AuthorityChange.Claimed));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AuthorityRecord("run", 2, double.NaN, "fsw", AuthorityChange.Claimed));
        }

        [Test]
        public void AnUnknownChangeIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AuthorityRecord("run", 2, 0.2, "fsw", (AuthorityChange)9, "why"));
        }
    }
}
