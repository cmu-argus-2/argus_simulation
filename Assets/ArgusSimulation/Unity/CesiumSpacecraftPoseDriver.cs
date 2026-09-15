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

        private void ApplyState(SpacecraftState state)
        {
            Vector3d position = state.PositionEcefMeters;
            globeAnchor.SetPositionEarthCenteredEarthFixed(position.X, position.Y, position.Z);

            if (!applyAttitude)
            {
                return;
            }

            Quaterniond rotation = state.BodyToEcef;
            globeAnchor.rotationGlobeFixed = new quaternion(
                (float)rotation.X,
                (float)rotation.Y,
                (float)rotation.Z,
                (float)rotation.W);
        }
    }
}
