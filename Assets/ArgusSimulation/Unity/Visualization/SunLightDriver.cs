using Argus.Simulation.Core;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // Aims the directional light along the SPICE Earth-to-Sun direction for each simulation state.
    // Parallax between Earth's center and the spacecraft (~3 arcminutes) is ignored.
    [RequireComponent(typeof(Light))]
    public sealed class SunLightDriver : MonoBehaviour
    {
        [SerializeField] private SimulationRunner runner;
        [SerializeField] private CesiumGeoreference georeference;

        public bool HasSunDirection { get; private set; }
        public Vector3d SunDirectionItrf93 { get; private set; }

        public void Configure(SimulationRunner simulationRunner)
        {
            if (runner != null && isActiveAndEnabled)
            {
                runner.StateProduced -= HandleState;
            }

            runner = simulationRunner;
            if (runner != null && isActiveAndEnabled)
            {
                runner.StateProduced += HandleState;
                if (runner.HasState)
                {
                    HandleState(runner.LastState);
                }
            }
        }

        private void OnEnable()
        {
            if (runner == null)
            {
                runner = FindAnyObjectByType<SimulationRunner>();
            }

            if (runner != null)
            {
                runner.StateProduced += HandleState;
            }
        }

        private void OnDisable()
        {
            if (runner != null)
            {
                runner.StateProduced -= HandleState;
            }
        }

        private void HandleState(SpacecraftState state)
        {
            if (!(runner.StateSource is AnalyticOrbitStateSource source) ||
                source.Ephemeris == null ||
                !source.Ephemeris.TryGetSample(state.SimulationTimeSeconds, out EphemerisSample environment))
            {
                return;
            }

            if (georeference == null)
            {
                georeference = FindAnyObjectByType<CesiumGeoreference>();
                if (georeference == null)
                {
                    return;
                }
            }

            georeference.Initialize();
            Vector3d toSun = environment.SunPositionItrf93Meters.Normalized();
            double3 toSunUnity = georeference.TransformEarthCenteredEarthFixedDirectionToUnity(
                new double3(toSun.X, toSun.Y, toSun.Z));
            Vector3 direction = new Vector3((float)toSunUnity.x, (float)toSunUnity.y, (float)toSunUnity.z);

            // A directional light shines along its forward axis, i.e. away from the Sun.
            transform.rotation = Quaternion.LookRotation(-direction.normalized);
            SunDirectionItrf93 = toSun;
            HasSunDirection = true;
        }
    }
}
