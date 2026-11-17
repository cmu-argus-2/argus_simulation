using System;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class CommandRecordTests
    {
        private static readonly DeviceCommandSet Sent = DeviceCommandSet.None(3, 0.3);

        // CORE-9: Held and Failsafe pass a command on, as Applied and Clamped do, and always say why.
        [TestCase(CommandOutcome.Held, "fsw")]
        [TestCase(CommandOutcome.Failsafe, null)]
        [TestCase(CommandOutcome.Clamped, null)]
        public void AnOutcomeThatPassesACommandWithAReasonNeedsBoth(CommandOutcome outcome, string controllerId)
        {
            Assert.That(new CommandRecord("run", 3, 0.3, outcome, Sent, Sent, "why", controllerId).Outcome, Is.EqualTo(outcome));
            Assert.Throws<ArgumentException>(() => new CommandRecord("run", 3, 0.3, outcome, Sent, null, "why", controllerId));
            Assert.Throws<ArgumentException>(() => new CommandRecord("run", 3, 0.3, outcome, Sent, Sent, null, controllerId));
        }

        // Nothing was sent for a held or failsafe step, so its requested set is empty; a held step
        // repeats the holder's command, and a failsafe step belongs to no controller.
        [Test]
        public void HeldNamesTheHolderAndFailsafeNoControllerAndNeitherWasSentAnything()
        {
            DeviceCommandSet torque = new DeviceCommandSet(3, 0.3, new[]
            {
                new DeviceCommand("torque.body", new DeviceValues.Builder().Add("torque_body_n_m", new Vector3d(1e-4, 0.0, 0.0)).Build())
            });

            Assert.Throws<ArgumentException>(() => new CommandRecord("run", 3, 0.3, CommandOutcome.Held, Sent, torque, "why"));
            Assert.Throws<ArgumentException>(() => new CommandRecord("run", 3, 0.3, CommandOutcome.Held, torque, torque, "why", "fsw"));
            Assert.Throws<ArgumentException>(() => new CommandRecord("run", 3, 0.3, CommandOutcome.Failsafe, Sent, Sent, "why", "fsw"));
            Assert.Throws<ArgumentException>(() => new CommandRecord("run", 3, 0.3, CommandOutcome.Failsafe, torque, Sent, "why"));
            Assert.That(new CommandRecord("run", 3, 0.3, CommandOutcome.Held, Sent, torque, "why", "fsw").Applied, Is.EqualTo(torque));
        }

        [Test]
        public void AppliedHasNoReasonAndRejectedPassesNothing()
        {
            Assert.That(new CommandRecord("run", 3, 0.3, CommandOutcome.Applied, Sent, Sent).Reason, Is.Null);
            Assert.Throws<ArgumentException>(() => new CommandRecord("run", 3, 0.3, CommandOutcome.Applied, Sent, Sent, "why"));
            Assert.That(new CommandRecord("run", 3, 0.3, CommandOutcome.Rejected, Sent, null, "why").Applied, Is.Null);
            Assert.Throws<ArgumentException>(() => new CommandRecord("run", 3, 0.3, CommandOutcome.Rejected, Sent, Sent, "why"));
        }

        // A value nobody listed fails loudly instead of being read as one of the known outcomes.
        [Test]
        public void AnUnknownOutcomeIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommandRecord("run", 3, 0.3, (CommandOutcome)5, Sent, Sent, "why"));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommandRecord("run", 3, 0.3, (CommandOutcome)5, Sent, null, "why"));
        }

        [Test]
        public void TheControllerIsKeptAndABlankOneIsRefused()
        {
            Assert.That(new CommandRecord("run", 3, 0.3, CommandOutcome.Applied, Sent, Sent, null, "fsw").ControllerId, Is.EqualTo("fsw"));
            Assert.That(new CommandRecord("run", 3, 0.3, CommandOutcome.Applied, Sent, Sent).ControllerId, Is.Null);
            Assert.Throws<ArgumentException>(() => new CommandRecord("run", 3, 0.3, CommandOutcome.Applied, Sent, Sent, null, " "));
        }
    }
}
