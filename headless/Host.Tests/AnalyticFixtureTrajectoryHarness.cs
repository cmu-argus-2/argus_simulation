using System;
using System.Globalization;
using System.IO;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Host.Tests
{
    // HOST-8 (#32), acceptance 3: writes the analytic fixture's trajectory so
    // Argus.Basilisk/scripts/check_reference_runs.py can measure its deviation from Basilisk (R4).
    // Explicit, because it needs inputs from a recorded r1-orbit-dt1 run:
    //   eval "$(python check_reference_runs.py r4 <r1 run dir> | sed 's/^/export /')"
    //   HOST8_R4_OUT=<csv> dotnet test headless/Host.Tests --filter Name=WriteAnalyticFixtureTrajectoryForR4
    //   python check_reference_runs.py r4 <r1 run dir> --fixture <csv>
    // The fixture starts on the circular orbit Basilisk is on at t = 0, in ITRF93 axes frozen at t = 0.
    public sealed class AnalyticFixtureTrajectoryHarness
    {
        [Test]
        [Explicit("HOST-8 R4 harness: needs HOST8_R4_* inputs from a recorded r1-orbit-dt1 run")]
        public void WriteAnalyticFixtureTrajectoryForR4()
        {
            CircularOrbitModel fixture = new CircularOrbitModel(
                new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
                Input("HOST8_R4_ALTITUDE_M"),
                Input("HOST8_R4_INCLINATION_DEG"),
                Input("HOST8_R4_RAAN_DEG"),
                Input("HOST8_R4_PHASE_DEG"));
            string output = Environment.GetEnvironmentVariable("HOST8_R4_OUT");
            Assert.That(output, Is.Not.Null.And.Not.Empty, "HOST8_R4_OUT");

            using StreamWriter writer = new StreamWriter(output);
            writer.WriteLine("t_s,x_m,y_m,z_m");
            for (int k = 0; k <= 1440; k++)
            {
                double t = k * 60.0;
                Vector3d position = fixture.Sample(k, t).PositionEcefMeters;
                writer.WriteLine(string.Format(
                    CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R}", t, position.X, position.Y, position.Z));
            }
        }

        private static double Input(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            Assert.That(value, Is.Not.Null.And.Not.Empty, name);
            return double.Parse(value, CultureInfo.InvariantCulture);
        }
    }
}
