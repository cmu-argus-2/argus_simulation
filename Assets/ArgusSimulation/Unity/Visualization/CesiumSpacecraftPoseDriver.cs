using Argus.Simulation.Core;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    [RequireComponent(typeof(CesiumGlobeAnchor))]
    public sealed class CesiumSpacecraftPoseDriver : MonoBehaviour
    {
        [SerializeField] private SimulationRunner runner;
        [SerializeField] private CesiumGlobeAnchor globeAnchor;
        [SerializeField] private bool applyAttitude = true;

        private Vector3 _manualAttitudeOffsetDegrees;

        public Vector3 ManualAttitudeOffsetDegrees => _manualAttitudeOffsetDegrees;

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

            if (runner != null)
            {
                runner.StateProduced += ApplyState;
            }
        }

        private void OnDisable()
        {
            if (runner != null)
            {
                runner.StateProduced -= ApplyState;
            }
        }

        public void Configure(SimulationRunner simulationRunner, CesiumGlobeAnchor anchor)
        {
            if (runner != null && isActiveAndEnabled)
            {
                runner.StateProduced -= ApplyState;
            }

            runner = simulationRunner;
            globeAnchor = anchor;

            if (runner != null && isActiveAndEnabled)
            {
                runner.StateProduced += ApplyState;
            }
        }

        public void NudgeAttitude(Vector3 deltaDegrees)
        {
            _manualAttitudeOffsetDegrees += deltaDegrees;
            _manualAttitudeOffsetDegrees.x = NormalizeAngle(_manualAttitudeOffsetDegrees.x);
            _manualAttitudeOffsetDegrees.y = NormalizeAngle(_manualAttitudeOffsetDegrees.y);
            _manualAttitudeOffsetDegrees.z = NormalizeAngle(_manualAttitudeOffsetDegrees.z);
            ReapplyLatestState();
        }

        public void ResetManualAttitude()
        {
            _manualAttitudeOffsetDegrees = Vector3.zero;
            ReapplyLatestState();
        }

        private void ApplyState(SpacecraftState state)
        {
            Vector3d position = state.PositionEcefMeters;
            globeAnchor.positionGlobeFixed = new double3(position.X, position.Y, position.Z);

            if (!applyAttitude)
            {
                return;
            }

            Quaterniond rotation = state.BodyToEcef;
            quaternion bodyToGlobeFixed = new quaternion(
                (float)rotation.X,
                (float)rotation.Y,
                (float)rotation.Z,
                (float)rotation.W);
            Quaternion offsetUnity = Quaternion.Euler(_manualAttitudeOffsetDegrees);
            quaternion offset = new quaternion(
                offsetUnity.x,
                offsetUnity.y,
                offsetUnity.z,
                offsetUnity.w);
            globeAnchor.rotationGlobeFixed = math.mul(bodyToGlobeFixed, offset);
        }

        private void ReapplyLatestState()
        {
            if (runner != null && runner.HasState)
            {
                ApplyState(runner.LastState);
            }
        }

        private static float NormalizeAngle(float angle)
        {
            return Mathf.Repeat(angle + 180f, 360f) - 180f;
        }
    }
}
