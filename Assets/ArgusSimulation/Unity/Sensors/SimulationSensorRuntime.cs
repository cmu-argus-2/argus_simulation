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

            ClearOutput();
        }

        private void Awake()
        {
            _manager = new SensorManager();
            _manager.Register(new IdealBodyRateSensorModel(
                BodyRateSensorId,
                BodyFrameId,
                Math.Max(0.001, bodyRateSamplePeriodSeconds)));
            ClearOutput();
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
            ClearOutput();
        }

        private void ClearOutput()
        {
            Latest = null;
        }

        // The run ID comes from the state source. The sensors restart only when a new run begins, so
        // a (run, sensor, sequence) key is never reused (D6).
        private void HandleState(SimulationState state)
        {
            if (state.RunId != RunId)
            {
                RunId = state.RunId;
                _manager.Reset(new SensorResetContext(RunId, state.Spacecraft.TimestampUtc, randomSeed));
            }

            SensorOutputSet output = _manager.Sample(new SensorSampleContext(RunId, state));
            Latest = output;
            OutputProduced?.Invoke(output);
        }
    }
}
