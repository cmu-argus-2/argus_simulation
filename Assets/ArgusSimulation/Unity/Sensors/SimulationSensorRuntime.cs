using System;
using Argus.Simulation.Core;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // TODO(G2): temporary bridge. Once the headless host streams states, SensorManager runs
    // there and Unity only displays the frames it receives.
    public sealed class SimulationSensorRuntime : MonoBehaviour
    {
        public const string BodyRateSensorId = "imu.body_rate";
        public const string BodyFrameId = "spacecraft_body";

        [SerializeField] private SimulationRunner runner;
        [SerializeField, Min(0.001f)] private double bodyRateSamplePeriodSeconds = 0.1;
        [SerializeField] private int randomSeed;

        private SensorManager _manager;
        private bool _resetPending;

        public event Action<SensorOutputSet> OutputProduced;

        public SensorOutputSet Latest { get; private set; }
        public bool HasOutput => Latest != null;
        public string RunId { get; private set; }

        public void Configure(SimulationRunner simulationRunner)
        {
            if (runner != null && isActiveAndEnabled)
            {
                Unsubscribe(runner);
            }

            runner = simulationRunner;
            if (runner != null && isActiveAndEnabled)
            {
                Subscribe(runner);
            }

            MarkResetPending();
        }

        private void Awake()
        {
            _manager = new SensorManager();
            _manager.Register(new IdealBodyRateSensorModel(
                BodyRateSensorId,
                BodyFrameId,
                Math.Max(0.001, bodyRateSamplePeriodSeconds)));
            MarkResetPending();
        }

        private void OnEnable()
        {
            if (runner == null)
            {
                runner = GetComponent<SimulationRunner>();
            }

            if (runner != null)
            {
                Subscribe(runner);
            }
        }

        private void OnDisable()
        {
            if (runner != null)
            {
                Unsubscribe(runner);
            }
        }

        private void Subscribe(SimulationRunner simulationRunner)
        {
            simulationRunner.StateProduced -= HandleState;
            simulationRunner.SimulationReset -= HandleSimulationReset;
            simulationRunner.StateProduced += HandleState;
            simulationRunner.SimulationReset += HandleSimulationReset;
        }

        private void Unsubscribe(SimulationRunner simulationRunner)
        {
            simulationRunner.StateProduced -= HandleState;
            simulationRunner.SimulationReset -= HandleSimulationReset;
        }

        private void HandleSimulationReset()
        {
            MarkResetPending();
        }

        private void MarkResetPending()
        {
            Latest = null;
            RunId = "unity-" + Guid.NewGuid().ToString("N");
            _resetPending = true;
        }

        private void HandleState(SpacecraftState state)
        {
            if (_resetPending)
            {
                _manager.Reset(new SensorResetContext(RunId, state.TimestampUtc, randomSeed));
                _resetPending = false;
            }

            SensorOutputSet output = _manager.Sample(new SensorSampleContext(RunId, state));
            Latest = output;
            OutputProduced?.Invoke(output);
        }
    }
}
