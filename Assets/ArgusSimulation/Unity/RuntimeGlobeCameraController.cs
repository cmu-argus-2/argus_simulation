using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

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
            Mouse mouse = Mouse.current;
            Vector2 mouseDelta = mouse != null ? mouse.delta.ReadValue() : Vector2.zero;
            bool pointerOverInterface = EventSystem.current != null &&
                EventSystem.current.IsPointerOverGameObject();

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
                _rasterController = FindAnyObjectByType<NasaGibsRasterController>();
            }

            if (!pointerOverInterface && mouse != null && mouse.leftButton.isPressed && _globeAnchor != null)
            {
                const float pointerDeltaToDegrees = 0.002f;
                _longitude = Mathf.Repeat(
                    (float)(_longitude - mouseDelta.x * orbitSensitivity * pointerDeltaToDegrees) + 180f,
                    360f) - 180f;
                _latitude = Mathf.Clamp(
                    (float)(_latitude + mouseDelta.y * orbitSensitivity * pointerDeltaToDegrees),
                    -85f,
                    85f);
                double3 position = _globeAnchor.longitudeLatitudeHeight;
                _globeAnchor.longitudeLatitudeHeight = new double3(_longitude, _latitude, position.z);
            }

            if (!pointerOverInterface && mouse != null && mouse.middleButton.isPressed)
            {
                const float pointerDeltaToPanScale = 0.01f;
                Vector3 pan = (-transform.right * mouseDelta.x - transform.up * mouseDelta.y) *
                    panSpeed * pointerDeltaToPanScale;
                transform.localPosition += pan;
            }

            float scroll = mouse != null ? mouse.scroll.ReadValue().y : 0f;
            if (!pointerOverInterface && Mathf.Abs(scroll) > Mathf.Epsilon)
            {
                const float scrollDeltaScale = 0.01f;
                Camera camera = GetComponent<Camera>();
                camera.fieldOfView = Mathf.Clamp(
                    camera.fieldOfView - scroll * zoomSensitivity * scrollDeltaScale,
                    minimumFieldOfView,
                    maximumFieldOfView);
            }
        }
    }
}
