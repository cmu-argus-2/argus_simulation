using System;
using System.Collections.Generic;

namespace Argus.Simulation.Core
{
    // Serves ephemeris samples from uniformly spaced knots (e.g. a SPICE reference file).
    // Earth orientation uses cubic Hermite interpolation on R and dR/dt, which keeps R
    // orthonormal to ~1e-15 at a 10 s spacing and makes dR/dt the exact derivative of the
    // interpolated R. Sun position is interpolated linearly (sub-meter error at 10 s).
    public sealed class TabulatedEphemerisProvider : IEphemerisProvider
    {
        private const double StepTolerance = 1e-9;

        private readonly EphemerisSample[] _knots;
        private readonly double _stepSeconds;

        public TabulatedEphemerisProvider(
            string sourceName,
            DateTimeOffset epochUtc,
            IReadOnlyList<EphemerisSample> knots)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("A source name is required.", nameof(sourceName));
            }

            if (knots == null || knots.Count < 2)
            {
                throw new ArgumentException("At least two knots are required.", nameof(knots));
            }

            _knots = new EphemerisSample[knots.Count];
            for (int index = 0; index < knots.Count; index++)
            {
                if (!knots[index].IsValid)
                {
                    throw new ArgumentException($"Knot {index} is not finite.", nameof(knots));
                }

                _knots[index] = knots[index];
            }

            _stepSeconds = _knots[1].SimulationTimeSeconds - _knots[0].SimulationTimeSeconds;
            if (_stepSeconds <= 0.0)
            {
                throw new ArgumentException("Knot times must increase.", nameof(knots));
            }

            for (int index = 1; index < _knots.Length; index++)
            {
                double expected = _knots[0].SimulationTimeSeconds + index * _stepSeconds;
                if (Math.Abs(_knots[index].SimulationTimeSeconds - expected) > StepTolerance)
                {
                    throw new ArgumentException($"Knot {index} breaks the uniform spacing.", nameof(knots));
                }
            }

            SourceName = sourceName;
            EpochUtc = epochUtc.ToUniversalTime();
        }

        public string SourceName { get; }
        public DateTimeOffset EpochUtc { get; }
        public double StepSeconds => _stepSeconds;
        public int KnotCount => _knots.Length;
        public double CoverageStartSeconds => _knots[0].SimulationTimeSeconds;
        public double CoverageEndSeconds => _knots[_knots.Length - 1].SimulationTimeSeconds;

        public bool TryGetSample(double simulationTimeSeconds, out EphemerisSample sample)
        {
            if (double.IsNaN(simulationTimeSeconds) ||
                simulationTimeSeconds < CoverageStartSeconds ||
                simulationTimeSeconds > CoverageEndSeconds)
            {
                sample = default;
                return false;
            }

            int index = (int)Math.Floor((simulationTimeSeconds - CoverageStartSeconds) / _stepSeconds);
            index = Math.Min(Math.Max(index, 0), _knots.Length - 2);
            EphemerisSample start = _knots[index];
            EphemerisSample end = _knots[index + 1];

            double h = _stepSeconds;
            double s = (simulationTimeSeconds - start.SimulationTimeSeconds) / h;
            double s2 = s * s;
            double s3 = s2 * s;

            // Hermite basis and its derivative with respect to s.
            double h00 = 2.0 * s3 - 3.0 * s2 + 1.0;
            double h10 = s3 - 2.0 * s2 + s;
            double h01 = -2.0 * s3 + 3.0 * s2;
            double h11 = s3 - s2;
            double d00 = 6.0 * s2 - 6.0 * s;
            double d10 = 3.0 * s2 - 4.0 * s + 1.0;
            double d01 = -6.0 * s2 + 6.0 * s;
            double d11 = 3.0 * s2 - 2.0 * s;

            Matrix3d rotation =
                start.J2000ToItrf93 * h00 +
                start.J2000ToItrf93Rate * (h10 * h) +
                end.J2000ToItrf93 * h01 +
                end.J2000ToItrf93Rate * (h11 * h);
            Matrix3d rotationRate =
                start.J2000ToItrf93 * (d00 / h) +
                start.J2000ToItrf93Rate * d10 +
                end.J2000ToItrf93 * (d01 / h) +
                end.J2000ToItrf93Rate * d11;

            Vector3d sun = start.SunPositionJ2000Meters +
                (end.SunPositionJ2000Meters - start.SunPositionJ2000Meters) * s;
            double offset = simulationTimeSeconds - start.SimulationTimeSeconds;

            sample = new EphemerisSample(
                simulationTimeSeconds,
                EpochUtc.AddSeconds(simulationTimeSeconds),
                start.EphemerisTimeSeconds + offset,
                rotation,
                rotationRate,
                sun);
            return true;
        }
    }
}
