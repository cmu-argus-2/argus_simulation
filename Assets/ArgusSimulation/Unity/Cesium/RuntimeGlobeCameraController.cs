using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Argus.Simulation.Unity
{
    public sealed class RuntimeGlobeCameraController : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float dragSensitivity = 2f;
        [SerializeField] private float panSpeed = 10_000f;
        [SerializeField] private float zoomSensitivity = 5f;
        [SerializeField] private float minimumFieldOfView = 10f;
        [SerializeField] private float maximumFieldOfView = 75f;

        private const double EarthRadiusMeters = 6371000.0;

        private Camera _camera;
        private CesiumGlobeAnchor _globeAnchor;
        private CesiumGeoreference _georeference;
        private NasaGibsRasterController _rasterController;

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

            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
            }

            if (_globeAnchor == null)
            {
                _globeAnchor = GetComponentInParent<CesiumGlobeAnchor>();
            }

            if (_georeference == null)
            {
                _georeference = GetComponentInParent<CesiumGeoreference>();
            }

            if (_rasterController == null)
            {
                _rasterController = FindAnyObjectByType<NasaGibsRasterController>();
            }

            if (!pointerOverInterface && mouse != null && mouse.leftButton.isPressed &&
                mouseDelta != Vector2.zero && _globeAnchor != null && _georeference != null)
            {
                DragGlobe(mouse.position.ReadValue(), mouseDelta);
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
                _camera.fieldOfView = Mathf.Clamp(
                    _camera.fieldOfView - scroll * zoomSensitivity * scrollDeltaScale,
                    minimumFieldOfView,
                    maximumFieldOfView);
            }
        }

        // Frame-to-frame arcball: both rays are cast from the same (current) camera pose, so the
        // result never depends on where Cesium's orientation adjustment left the camera last frame.
        private void DragGlobe(Vector2 pointer, Vector2 pointerDelta)
        {
            double3 cameraOriginEcef = CameraOriginEcef();
            double3? previous = GlobeDirectionUnderPointer(pointer - pointerDelta, cameraOriginEcef);
            double3? current = GlobeDirectionUnderPointer(pointer, cameraOriginEcef);
            if (!previous.HasValue || !current.HasValue)
            {
                return;
            }

            // Rotating the globe previous→current keeps the grabbed point under the pointer (at
            // sensitivity 1); moving the observer by the inverse rotation looks identical.
            double3 lonLatHeight = _globeAnchor.longitudeLatitudeHeight;
            double3 observerDirection = LongitudeLatitudeToUnitDirection(lonLatHeight.x, lonLatHeight.y);
            double3 d = RotateFromTo(observerDirection, current.Value, previous.Value, dragSensitivity);

            double longitude = math.degrees(math.atan2(d.y, d.x));
            double latitude = math.degrees(math.atan2(d.z, math.sqrt(d.x * d.x + d.y * d.y)));
            _globeAnchor.longitudeLatitudeHeight = new double3(longitude, latitude, lonLatHeight.z);
        }

        // Built from the anchor's double-precision ECEF position plus the camera's small local
        // offset, because the camera's float world position is only accurate to meters out here.
        private double3 CameraOriginEcef()
        {
            double3 origin = _globeAnchor.positionGlobeFixed;
            if (_globeAnchor.transform == transform || transform.parent == null)
            {
                return origin;
            }

            Vector3 offsetWorld = transform.parent.TransformVector(transform.localPosition);
            Vector3 offsetLocal = _georeference.transform.InverseTransformVector(offsetWorld);
            return origin + _georeference.TransformUnityDirectionToEarthCenteredEarthFixed(ToDouble3(offsetLocal));
        }

        // Unit direction from Earth's center to where the pointer ray meets a sphere approximating
        // Earth. Past the horizon, falls back to the ray's closest point to the center so the drag
        // keeps tracking instead of stalling at the silhouette.
        private double3? GlobeDirectionUnderPointer(Vector2 screenPosition, double3 originEcef)
        {
            double3 directionEcef = PointerRayDirectionEcef(screenPosition);

            double b = math.dot(originEcef, directionEcef);
            double c = math.dot(originEcef, originEcef) - EarthRadiusMeters * EarthRadiusMeters;
            double discriminant = b * b - c;

            if (discriminant >= 0.0)
            {
                double sqrtDiscriminant = math.sqrt(discriminant);
                double t = -b - sqrtDiscriminant;
                if (t < 0.0)
                {
                    t = -b + sqrtDiscriminant;
                }

                return t < 0.0 ? null : math.normalize(originEcef + directionEcef * t);
            }

            double3 closestPoint = originEcef - directionEcef * b;
            return math.length(closestPoint) < 1.0 ? null : math.normalize(closestPoint);
        }

        // Computed from the projection parameters and camera rotation rather than
        // Camera.ScreenPointToRay, whose float world matrices add visible noise at orbital distances.
        private double3 PointerRayDirectionEcef(Vector2 screenPosition)
        {
            Rect pixelRect = _camera.pixelRect;
            float ndcX = (screenPosition.x - pixelRect.x) / pixelRect.width * 2f - 1f;
            float ndcY = (screenPosition.y - pixelRect.y) / pixelRect.height * 2f - 1f;
            float tanHalfFov = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            Vector3 cameraSpace = new Vector3(ndcX * tanHalfFov * _camera.aspect, ndcY * tanHalfFov, 1f);

            Vector3 worldDirection = transform.rotation * cameraSpace;
            Vector3 localDirection = _georeference.transform.InverseTransformDirection(worldDirection);
            return math.normalize(
                _georeference.TransformUnityDirectionToEarthCenteredEarthFixed(ToDouble3(localDirection)));
        }

        // Rodrigues rotation of v about the from→to axis, by `scale` times the from→to angle.
        private static double3 RotateFromTo(double3 v, double3 from, double3 to, double scale)
        {
            double3 axis = math.cross(from, to);
            double axisLength = math.length(axis);
            if (axisLength < 1e-15)
            {
                return v;
            }

            axis /= axisLength;
            double angle = math.atan2(axisLength, math.dot(from, to)) * scale;
            double cosAngle = math.cos(angle);
            return v * cosAngle + math.cross(axis, v) * math.sin(angle) +
                axis * math.dot(axis, v) * (1.0 - cosAngle);
        }

        private static double3 LongitudeLatitudeToUnitDirection(double longitudeDegrees, double latitudeDegrees)
        {
            double longitudeRadians = math.radians(longitudeDegrees);
            double latitudeRadians = math.radians(latitudeDegrees);
            double cosLatitude = math.cos(latitudeRadians);
            return new double3(
                cosLatitude * math.cos(longitudeRadians),
                cosLatitude * math.sin(longitudeRadians),
                math.sin(latitudeRadians));
        }

        private static double3 ToDouble3(Vector3 vector)
        {
            return new double3(vector.x, vector.y, vector.z);
        }
    }
}
