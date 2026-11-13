using Argus.Simulation.Core;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // Places the CubeSat at the host's truth pose. The stream is decimated (CON-1), so the pose is
    // interpolated between the last two received states, one arrival interval behind the latest:
    // at each arrival it starts from the previous state and reaches the new one after the interval
    // they arrived apart. Published states are never changed. After a gap longer than
    // MaximumInterpolationGapSeconds (a reconnect) it jumps to the new state rather than gliding
    // across the gap. Hold shows one state exactly, for a capture whose images must match its
    // metadata.
    [RequireComponent(typeof(CesiumGlobeAnchor))]
    public sealed class CesiumSpacecraftPoseDriver : MonoBehaviour
    {
        [SerializeField] private SimulationRunner runner;
        [SerializeField] private CesiumGlobeAnchor globeAnchor;
        [SerializeField] private bool applyAttitude = true;

        private const double MaximumInterpolationGapSeconds = 1.0;

        private SpacecraftState _from;
        private SpacecraftState _to;
        private double _fromArrivalSeconds;
        private double _toArrivalSeconds;
        private int _received;
        private bool _isHeld;

        public bool IsHeld => _isHeld;

        private void Reset()
        {
            globeAnchor = GetComponent<CesiumGlobeAnchor>();
        }

        private void OnEnable()
        {
            if (globeAnchor == null)
            {
                globeAnchor = GetComponent<CesiumGlobeAnchor>();
            }

            globeAnchor.adjustOrientationForGlobeWhenMoving = false;
            globeAnchor.detectTransformChanges = false;
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        public void Configure(SimulationRunner simulationRunner, CesiumGlobeAnchor anchor)
        {
            if (isActiveAndEnabled)
            {
                Unsubscribe();
            }

            runner = simulationRunner;
            globeAnchor = anchor;
            ClearHistory();
            if (isActiveAndEnabled)
            {
                Subscribe();
            }
        }

        // Shows exactly this state until Release, while states keep arriving.
        public void Hold(SimulationState state)
        {
            _isHeld = true;
            Apply(state.Spacecraft.PositionEcefMeters, state.Spacecraft.BodyToEcef);
        }

        public void Release()
        {
            _isHeld = false;
        }

        // In Update, so cameras and anchors that follow the CubeSat in LateUpdate see this frame's pose.
        private void Update()
        {
            if (_isHeld || _received == 0)
            {
                return;
            }

            if (_received == 1)
            {
                Apply(_to.PositionEcefMeters, _to.BodyToEcef);
                return;
            }

            double interval = _toArrivalSeconds - _fromArrivalSeconds;
            if (interval > MaximumInterpolationGapSeconds)
            {
                Apply(_to.PositionEcefMeters, _to.BodyToEcef);
                return;
            }

            double t = interval > 0.0
                ? math.saturate((Time.realtimeSinceStartupAsDouble - _toArrivalSeconds) / interval)
                : 1.0;
            Vector3d position = _from.PositionEcefMeters + (_to.PositionEcefMeters - _from.PositionEcefMeters) * t;
            Quaternion rotation = Quaternion.Slerp(ToUnity(_from.BodyToEcef), ToUnity(_to.BodyToEcef), (float)t);
            Apply(position, new Quaterniond(rotation.x, rotation.y, rotation.z, rotation.w));
        }

        private void HandleState(SimulationState state)
        {
            _from = _to;
            _fromArrivalSeconds = _toArrivalSeconds;
            _to = state.Spacecraft;
            _toArrivalSeconds = Time.realtimeSinceStartupAsDouble;
            _received = Mathf.Min(_received + 1, 2);
        }

        private void ClearHistory()
        {
            _received = 0;
        }

        private void Apply(Vector3d position, Quaterniond bodyToEcef)
        {
            globeAnchor.positionGlobeFixed = new double3(position.X, position.Y, position.Z);
            if (applyAttitude)
            {
                globeAnchor.rotationGlobeFixed = new quaternion(
                    (float)bodyToEcef.X,
                    (float)bodyToEcef.Y,
                    (float)bodyToEcef.Z,
                    (float)bodyToEcef.W);
            }
        }

        private void Subscribe()
        {
            if (runner != null)
            {
                runner.StateProduced += HandleState;
                runner.HistoryReset += ClearHistory;
            }
        }

        private void Unsubscribe()
        {
            if (runner != null)
            {
                runner.StateProduced -= HandleState;
                runner.HistoryReset -= ClearHistory;
            }
        }

        private static Quaternion ToUnity(Quaterniond value) =>
            new Quaternion((float)value.X, (float)value.Y, (float)value.Z, (float)value.W);
    }
}
