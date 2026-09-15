using UnityEngine;

namespace Argus.Simulation.Unity
{
    public sealed class RuntimeGlobeCameraController : MonoBehaviour
    {
        [SerializeField] private float orbitSensitivity = 0.2f;
        [SerializeField] private float panSpeed = 10_000f;
        [SerializeField] private float zoomSensitivity = 5f;
        [SerializeField] private float minimumFieldOfView = 10f;
        [SerializeField] private float maximumFieldOfView = 75f;

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

            if (Input.GetMouseButton(0))
            {
                transform.Rotate(Vector3.up, -mouseDelta.x * orbitSensitivity, Space.Self);
                transform.Rotate(Vector3.right, mouseDelta.y * orbitSensitivity, Space.Self);
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
    }
}