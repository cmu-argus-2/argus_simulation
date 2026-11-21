using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class AuthorityRecordTests
    {
        // CORE-9: a refusal or a loss says why; a claim does not, and a release only when the run's
        // end made it.
        [TestCase(AuthorityChange.Refused)]
        [TestCase(AuthorityChange.Lost)]
        public void ARefusalOrALossNeedsAReason(AuthorityChange change)
        {
            Assert.That(new AuthorityRecord("run", 2, 0.2, "fsw", change, "why").Reason, Is.EqualTo("why"));
            Assert.Throws<ArgumentException>(() => new AuthorityRecord("run", 2, 0.2, "fsw", change));
        }

        [Test]
        public void AClaimHasNoReason()
        {
            Assert.That(new AuthorityRecord("run", 2, 0.2, "fsw", AuthorityChange.Claimed).Reason, Is.Null);
            Assert.Throws<ArgumentException>(() => new AuthorityRecord("run", 2, 0.2, "fsw", AuthorityChange.Claimed, "why"));
        }

        // A release by the holder has no reason; a release the gateway makes when the run ends says how
        // it ended (#156 review).
        [Test]
        public void AReleaseHasAReasonOnlyWhenTheRunEndedIt()
        {
            Assert.That(new AuthorityRecord("run", 2, 0.2, "fsw", AuthorityChange.Released).Reason, Is.Null);
            Assert.That(new AuthorityRecord("run", 2, 0.2, "fsw", AuthorityChange.Released, "the run was reset").Reason, Is.EqualTo("the run was reset"));
            Assert.Throws<ArgumentException>(() => new AuthorityRecord("run", 2, 0.2, "fsw", AuthorityChange.Released, " "));
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
