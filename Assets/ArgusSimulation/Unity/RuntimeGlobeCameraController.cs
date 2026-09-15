using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    public sealed class RuntimeGlobeCameraController : MonoBehaviour
    {
        [SerializeField] private float orbitSensitivity = 100f;
        [SerializeField] private float panSpeed = 10_000f;
        [SerializeField] private float zoomSensitivity = 5f;
        [SerializeField] private float minimumFieldOfView = 10f;
        [SerializeField] private float maximumFieldOfView = 75f;

        private CesiumGlobeAnchor _globeAnchor;
        private NasaGibsRasterController _rasterController;
        private double _longitude;
        private double _latitude;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AddToMainCamera()
        {
            Camera mainCamera = Camera.main;
            if (mainCamera != null && mainCamera.GetComponent<RuntimeGlobeCameraController>() == null)
            {
                mainCamera.gameObject.AddComponent<RuntimeGlobeCameraController>();
            }
        }

        private void Update()
        {
            Vector2 mouseDelta = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));

            if (_globeAnchor == null)
            {
                _globeAnchor = GetComponentInParent<CesiumGlobeAnchor>();
                if (_globeAnchor != null)
                {
                    double3 position = _globeAnchor.longitudeLatitudeHeight;
                    _longitude = position.x;
                    _latitude = position.y;
                }
            }

            if (_rasterController == null)
            {
                _rasterController = FindFirstObjectByType<NasaGibsRasterController>();
            }

            if (Input.GetMouseButton(0) && _globeAnchor != null)
            {
                _longitude = Mathf.Repeat((float)(_longitude - mouseDelta.x * orbitSensitivity) + 180f, 360f) - 180f;
                _latitude = Mathf.Clamp((float)(_latitude + mouseDelta.y * orbitSensitivity), -85f, 85f);
                double3 position = _globeAnchor.longitudeLatitudeHeight;
                _globeAnchor.longitudeLatitudeHeight = new double3(_longitude, _latitude, position.z);
            }

            if (Input.GetMouseButton(2))
            {
                Vector3 pan = (-transform.right * mouseDelta.x - transform.up * mouseDelta.y) * panSpeed;
                transform.localPosition += pan * Time.deltaTime;
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > Mathf.Epsilon)
            {
                Camera camera = GetComponent<Camera>();
                camera.fieldOfView = Mathf.Clamp(
                    camera.fieldOfView - scroll * zoomSensitivity,
                    minimumFieldOfView,
                    maximumFieldOfView);
            }

        }

        private void OnGUI()
        {
            if (_rasterController == null)
            {
                return;
            }

            GUI.Label(new Rect(12f, 12f, 320f, 24f), "DAY + NIGHT LIGHTS");
        }
    }
}