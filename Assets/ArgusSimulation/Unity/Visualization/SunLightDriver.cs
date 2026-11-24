using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class SunLightDriver : MonoBehaviour
    {
        [SerializeField] private SimulationRunner runner;
        [SerializeField] private CesiumGeoreference georeference;
        [SerializeField] private Light sunlight;
        [SerializeField] private NasaGibsRasterController rasterController;
        private Quaternion fallbackRotation = Quaternion.identity;
        public bool HasPhysicalLighting { get; private set; }

        public void Configure(SimulationRunner source, CesiumGeoreference frame,
            Light light, NasaGibsRasterController raster)
        {
            runner = source;
            georeference = frame;
            sunlight = light;
            rasterController = raster;
            fallbackRotation = light.transform.rotation;
        }

        private void Awake()
        {
            if (sunlight != null) fallbackRotation = sunlight.transform.rotation;
        }

        // Reproject each frame because Cesium's origin may move even while state is held.
        private void Update()
        {
            HasPhysicalLighting = false;
            if (sunlight == null) return;
            if (runner != null && runner.HasState && georeference != null &&
                runner.LastState.Environment.HasValue && runner.LastState.Environment.Value.IsValid)
            {
                var sun = runner.LastState.Environment.Value.SunPositionItrf93Meters;
                var local = georeference.TransformEarthCenteredEarthFixedDirectionToUnity(
                    new double3(sun.X, sun.Y, sun.Z));
                Vector3 towardSun = georeference.transform.TransformDirection(
                    new Vector3((float)local.x, (float)local.y, (float)local.z)).normalized;
                if (towardSun.sqrMagnitude > 0f)
                {
                    Vector3 up = Mathf.Abs(Vector3.Dot(towardSun, Vector3.up)) > 0.99f
                        ? Vector3.right : Vector3.up;
                    sunlight.transform.rotation = Quaternion.LookRotation(-towardSun, up);
                    rasterController?.SetSunDirection(towardSun);
                    HasPhysicalLighting = true;
                    return;
                }
            }
            sunlight.transform.rotation = fallbackRotation;
            rasterController?.ClearSunDirection();
        }

        private void OnDisable()
        {
            HasPhysicalLighting = false;
            if (sunlight != null) sunlight.transform.rotation = fallbackRotation;
            rasterController?.ClearSunDirection();
        }
    }
}
