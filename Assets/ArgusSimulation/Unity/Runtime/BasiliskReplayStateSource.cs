using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // Development-only, exact-sample open-loop playback. No actuator feedback.
    public sealed class BasiliskReplayStateSource : MonoBehaviour, ISpacecraftStateSource, IGyroMeasurementSource
    {
        public const string Profile = "synthetic-earth-fixed-aligned-at-t0-v1";
        [SerializeField] private string jsonlPath = "dynamics/basilisk/experiments/results/synchronized_gyro/synthetic_fixed_samples.jsonl";
        [SerializeField] private bool allowSyntheticEarthFixed;
        private readonly List<SpacecraftState> states = new List<SpacecraftState>();
        private readonly List<Vector3d> gyros = new List<Vector3d>();
        public int Count => states.Count;
        public string SourceName => "BASILISK REPLAY";
        public double StartTimeSeconds => states[0].SimulationTimeSeconds;
        public double StepSeconds => states[1].SimulationTimeSeconds - StartTimeSeconds;

        private void Start()
        {
            var runner = GetComponent<SimulationRunner>();
            if (runner != null && ReferenceEquals(runner.StateSource, this) && Count == 0)
                LoadReplayAndConfigureRunner();
        }

        [Serializable] private sealed class Record
        {
            public string schema;
            public string frame_profile;
            public long sequence;
            public double simulation_time_s;
            public string timestamp_utc;
            public Truth truth;
            public Measurements measurements;
        }
        [Serializable] private sealed class Truth
        {
            public double[] position_fixed_m;
            public double[] velocity_fixed_m_s;
            public double[] body_to_fixed_xyzw;
            public double[] angular_velocity_body_rad_s;
        }
        [Serializable] private sealed class Measurements { public double[] gyro_body_rad_s; }

        public void LoadJsonLines(string text, bool acceptSyntheticFrame)
        {
            states.Clear();
            gyros.Clear();
            if (!acceptSyntheticFrame)
                throw new InvalidOperationException("Explicit synthetic-frame opt-in required; this is not astronomical ECEF.");
            var loaded = new List<SpacecraftState>();
            var measurements = new List<Vector3d>();
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    Record r = JsonUtility.FromJson<Record>(line);
                    if (r == null || r.schema != "basilisk-frame-replay-demo-v1" ||
                        r.frame_profile != Profile || r.truth == null || r.measurements == null)
                        throw new FormatException("Unsupported replay schema/profile or missing payload.");
                    if (r.timestamp_utc == null || !r.timestamp_utc.EndsWith("Z", StringComparison.Ordinal) ||
                        !DateTimeOffset.TryParse(r.timestamp_utc, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
                        throw new FormatException("Expected UTC timestamp ending in Z.");
                    double[] q = r.truth.body_to_fixed_xyzw;
                    if (q == null || q.Length != 4) throw new FormatException("Expected xyzw quaternion.");
                    var rotation = new Quaterniond(q[0], q[1], q[2], q[3]);
                    var state = new SpacecraftState(r.sequence, r.simulation_time_s, utc,
                        Vector(r.truth.position_fixed_m), Vector(r.truth.velocity_fixed_m_s),
                        rotation, Vector(r.truth.angular_velocity_body_rad_s));
                    if (!state.IsValid || state.SimulationTimeSeconds < 0 || !rotation.IsUnit)
                        throw new FormatException("Invalid state or non-unit quaternion.");
                    if (loaded.Count > 0)
                    {
                        var previous = loaded[loaded.Count - 1];
                        double dt = state.SimulationTimeSeconds - previous.SimulationTimeSeconds;
                        if (dt <= 0 || state.Sequence != previous.Sequence + 1 ||
                            Math.Abs((utc - previous.TimestampUtc).TotalSeconds - dt) > 1e-6 ||
                            (loaded.Count > 1 && Math.Abs(dt - (loaded[1].SimulationTimeSeconds - loaded[0].SimulationTimeSeconds)) > 1e-8))
                            throw new FormatException("Replay requires consecutive, uniform samples and a consistent UTC clock.");
                    }
                    loaded.Add(state);
                    measurements.Add(Vector(r.measurements.gyro_body_rad_s));
                }
            }
            if (loaded.Count < 2) throw new FormatException("At least two replay samples are required.");
            states.AddRange(loaded);
            gyros.AddRange(measurements);
        }

        private static Vector3d Vector(double[] values)
        {
            if (values == null || values.Length != 3) throw new FormatException("Expected a 3-vector.");
            var result = new Vector3d(values[0], values[1], values[2]);
            if (!result.IsFinite) throw new FormatException("Non-finite vector.");
            return result;
        }

        private int IndexAt(double time)
        {
            if (Count < 2 || double.IsNaN(time) || double.IsInfinity(time)) return -1;
            double index = Math.Round((time - StartTimeSeconds) / StepSeconds);
            if (index < 0 || index >= Count) return -1;
            int i = (int)index;
            return Math.Abs(states[i].SimulationTimeSeconds - time) <= 1e-8 ? i : -1;
        }

        public bool TryGetState(long sequence, double simulationTimeSeconds, out SpacecraftState state)
        {
            state = default;
            int i = IndexAt(simulationTimeSeconds);
            if (i < 0 || sequence < 0) return false;
            var source = states[i];
            // Runner sequence is independent of the file's original sequence.
            state = new SpacecraftState(sequence, source.SimulationTimeSeconds, source.TimestampUtc,
                source.PositionEcefMeters, source.VelocityEcefMetersPerSecond,
                source.BodyToEcef, source.AngularVelocityBodyRadiansPerSecond);
            return true;
        }

        // Exposed separately: never put noisy gyro into the truth state.
        public bool TryGetGyroMeasurement(
            long sequence,
            double simulationTimeSeconds,
            out SensorFrame<Vector3d> measurement)
        {
            int i = IndexAt(simulationTimeSeconds);
            if (i < 0 || sequence < 0)
            {
                measurement = default;
                return false;
            }
            SpacecraftState state = states[i];
            measurement = new SensorFrame<Vector3d>(
                "imu.gyro", sequence, simulationTimeSeconds, state.TimestampUtc,
                SensorFrameStatus.Valid, "basilisk-replay", gyros[i]);
            return true;
        }

        [ContextMenu("Load Replay And Configure Runner")]
        public void LoadReplayAndConfigureRunner()
        {
            var runner = GetComponent<SimulationRunner>();
            if (runner == null) throw new InvalidOperationException("Add this component beside SimulationRunner.");
            string path = Path.IsPathRooted(jsonlPath) ? jsonlPath :
                Path.Combine(Path.GetDirectoryName(Application.dataPath), jsonlPath);
            LoadJsonLines(File.ReadAllText(path), allowSyntheticEarthFixed);
            runner.Configure(this, StepSeconds, StartTimeSeconds);
            runner.ResetSimulation();

            var trail = FindAnyObjectByType<OrbitTrailRenderer>();
            if (trail != null)
            {
                trail.Configure(runner);
                trail.BuildTrail();
            }

            Debug.Log($"Loaded {Count} Basilisk samples. Synthetic fixed frame; open-loop only.", this);
        }
    }
}
