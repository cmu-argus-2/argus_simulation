using System;
using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using NUnit.Framework;
using UnityEngine;

namespace Argus.Simulation.Tests
{
    public sealed class BasiliskReplayTests
    {
        private GameObject host;
        private BasiliskReplayStateSource source;
        private static string Sample(int index) =>
            "{\"schema\":\"basilisk-frame-replay-demo-v1\",\"frame_profile\":\"" + BasiliskReplayStateSource.Profile +
            "\",\"sequence\":" + index + ",\"simulation_time_s\":" + (index == 1 ? "0.05" : "0.10") +
            ",\"timestamp_utc\":\"2026-09-27T00:00:00." + (index == 1 ? "050" : "100") +
            "Z\",\"truth\":{\"position_fixed_m\":[7000000,0,0],\"velocity_fixed_m_s\":[0,7000,0]," +
            "\"body_to_fixed_xyzw\":[0,0,0,1],\"angular_velocity_body_rad_s\":[0.01,0,0]}," +
            "\"measurements\":{\"gyro_body_rad_s\":[0.012,0,0]}}";
        private static string Data => Sample(1) + "\n" + Sample(2);

        [SetUp] public void SetUp()
        {
            host = new GameObject("replay-test");
            source = host.AddComponent<BasiliskReplayStateSource>();
        }
        [TearDown] public void TearDown() => UnityEngine.Object.DestroyImmediate(host);

        [Test] public void ExactSamplesKeepTruthAndMeasurementsSeparate()
        {
            source.LoadJsonLines(Data, true);
            Assert.That(source.TryGetState(0, .05, out var state), Is.True);
            Assert.That(state.Sequence, Is.EqualTo(0));
            Assert.That(state.SimulationTimeSeconds, Is.EqualTo(.05));
            Assert.That(state.AngularVelocityBodyRadiansPerSecond.X, Is.EqualTo(.01));
            Assert.That(source.TryGetGyroMeasurement(0, .05, out var gyro), Is.True);
            Assert.That(gyro.Payload.X, Is.EqualTo(.012));
            Assert.That(gyro.Source, Is.EqualTo("basilisk-replay"));
            Assert.That(source.TryGetState(0, 0, out _), Is.False);
            Assert.That(source.TryGetState(0, .075, out _), Is.False);
            Assert.That(source.TryGetState(2, .15, out _), Is.False);
        }

        [Test] public void SensorSuiteUsesRecordedMeasurementNotTruth()
        {
            source.LoadJsonLines(Data, true);
            var runner = host.AddComponent<SimulationRunner>();
            var sensors = host.AddComponent<MockSensorSuite>();
            runner.Configure(source, source.StepSeconds, source.StartTimeSeconds);
            runner.ResetSimulation();
            sensors.Configure(runner);

            Assert.That(runner.StepOnce(), Is.True);
            Assert.That(sensors.Latest.State.AngularVelocityBodyRadiansPerSecond.X, Is.EqualTo(.01));
            Assert.That(sensors.Latest.GyroMeasurement.Payload.X, Is.EqualTo(.012));
            Assert.That(sensors.Latest.GyroMeasurement.Status, Is.EqualTo(SensorFrameStatus.Valid));
        }

        [Test] public void RejectsOptOutDuplicatesAndWrongProfile()
        {
            Assert.Throws<InvalidOperationException>(() => source.LoadJsonLines(Data, false));
            Assert.Throws<FormatException>(() => source.LoadJsonLines(Sample(1) + "\n" + Sample(1), true));
            Assert.Throws<FormatException>(() => source.LoadJsonLines(Data.Replace(BasiliskReplayStateSource.Profile, "unknown"), true));
            Assert.That(source.Count, Is.Zero);
        }

        [Test] public void RunnerStartsAtRecordedTimeAndResetReplays()
        {
            source.LoadJsonLines(Data, true);
            var runner = host.AddComponent<SimulationRunner>();
            runner.Configure(source, source.StepSeconds, source.StartTimeSeconds);
            runner.ResetSimulation();
            Assert.That(runner.StepOnce(), Is.True);
            Assert.That(runner.LastState.SimulationTimeSeconds, Is.EqualTo(.05));
            Assert.That(runner.StepOnce(), Is.True);
            Assert.That(runner.StepOnce(), Is.False);
            runner.ResetSimulation();
            Assert.That(runner.StepOnce(), Is.True);
            Assert.That(runner.LastState.Sequence, Is.EqualTo(0));
        }
    }
}
