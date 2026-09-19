using UnityEngine;

namespace Argus.Simulation.Unity
{
    [DisallowMultipleComponent]
    public sealed class CubeSatVisualModel : MonoBehaviour
    {
        public const int OrbitMarkerLayer = 8;
        public const int SpacecraftModelLayer = 9;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void UpgradeExistingFoundationScene()
        {
            GameObject spacecraft = GameObject.Find("CubeSat Truth Pose");
            if (spacecraft != null && spacecraft.GetComponent<CubeSatVisualModel>() == null)
            {
                spacecraft.AddComponent<CubeSatVisualModel>();
            }
        }

        private void Awake()
        {
            BuildIfNeeded();
        }

        public void BuildIfNeeded()
        {
            if (transform.Find("CubeSat Visual Model") != null)
            {
                return;
            }

            Transform placeholder = transform.Find("CubeSat Body (placeholder)");
            if (placeholder != null)
            {
                placeholder.gameObject.SetActive(false);
            }

            Material chassisMaterial = CreateMaterial(
                "Argus 1U Chassis",
                new Color(0.68f, 0.72f, 0.76f),
                0.72f,
                0.42f);
            Material railMaterial = CreateMaterial(
                "Argus 1U Rails",
                new Color(0.78f, 0.57f, 0.18f),
                0.82f,
                0.35f);
            Material panelMaterial = CreateMaterial(
                "Argus Body Solar Cells",
                new Color(0.025f, 0.12f, 0.30f),
                0.30f,
                0.72f);
            Material cameraMaterial = CreateMaterial(
                "Argus Camera Housing",
                new Color(0.08f, 0.10f, 0.12f),
                0.55f,
                0.62f);
            Material lensMaterial = CreateMaterial(
                "Argus Camera Lens",
                new Color(0.015f, 0.035f, 0.08f),
                0.20f,
                0.94f);

            GameObject model = new GameObject("CubeSat Visual Model");
            model.transform.SetParent(transform, false);
            model.layer = SpacecraftModelLayer;

            // 1U frame: approximately 10 cm on each axis. Body +Z points nadir;
            // the four operational cameras point along the lateral +/-X and +/-Y axes.
            CreateCube(model.transform, "1U Chassis", Vector3.zero,
                new Vector3(0.086f, 0.086f, 0.086f), chassisMaterial);
            CreateCube(model.transform, "Zenith Plate", new Vector3(0f, 0f, -0.052f),
                new Vector3(0.105f, 0.105f, 0.006f), railMaterial);
            CreateCube(model.transform, "Nadir Plate", new Vector3(0f, 0f, 0.052f),
                new Vector3(0.105f, 0.105f, 0.006f), railMaterial);

            foreach (float x in new[] { -0.048f, 0.048f })
            {
                foreach (float y in new[] { -0.048f, 0.048f })
                {
                    CreateCube(model.transform, "Structural Rail", new Vector3(x, y, 0f),
                        new Vector3(0.008f, 0.008f, 0.11f), railMaterial);
                }
            }

            BuildBodyPanel(model.transform, Vector3.right, panelMaterial);
            BuildBodyPanel(model.transform, Vector3.left, panelMaterial);
            BuildBodyPanel(model.transform, Vector3.up, panelMaterial);
            BuildBodyPanel(model.transform, Vector3.down, panelMaterial);

            BuildSideCamera(
                model.transform,
                "Forward +X Camera",
                Vector3.right,
                Quaternion.Euler(0f, 0f, -90f),
                cameraMaterial,
                lensMaterial);
            BuildSideCamera(
                model.transform,
                "Aft -X Camera",
                Vector3.left,
                Quaternion.Euler(0f, 0f, 90f),
                cameraMaterial,
                lensMaterial);
            BuildSideCamera(
                model.transform,
                "Starboard +Y Camera",
                Vector3.up,
                Quaternion.identity,
                cameraMaterial,
                lensMaterial);
            BuildSideCamera(
                model.transform,
                "Port -Y Camera",
                Vector3.down,
                Quaternion.Euler(180f, 0f, 0f),
                cameraMaterial,
                lensMaterial);

            BuildOrbitMarker();
        }

        private static void BuildBodyPanel(Transform parent, Vector3 outward, Material material)
        {
            bool xFace = Mathf.Abs(outward.x) > 0.5f;
            Vector3 scale = xFace
                ? new Vector3(0.004f, 0.070f, 0.070f)
                : new Vector3(0.070f, 0.004f, 0.070f);
            CreateCube(
                parent,
                "Body-Mounted Solar Panel",
                outward * 0.0445f,
                scale,
                material);
        }

        private static void BuildSideCamera(
            Transform parent,
            string name,
            Vector3 outward,
            Quaternion lensRotation,
            Material housingMaterial,
            Material lensMaterial)
        {
            bool xFace = Mathf.Abs(outward.x) > 0.5f;
            Vector3 housingScale = xFace
                ? new Vector3(0.010f, 0.032f, 0.032f)
                : new Vector3(0.032f, 0.010f, 0.032f);
            CreateCube(parent, name + " Housing", outward * 0.051f, housingScale, housingMaterial);
            CreateCylinder(
                parent,
                name + " Lens",
                outward * 0.060f,
                new Vector3(0.011f, 0.004f, 0.011f),
                lensRotation,
                lensMaterial);
        }

        private void BuildOrbitMarker()
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "Orbit Position Marker";
            marker.transform.SetParent(transform, false);
            marker.transform.localScale = Vector3.one * 90_000f;
            marker.layer = OrbitMarkerLayer;
            Collider markerCollider = marker.GetComponent<Collider>();
            if (markerCollider != null)
            {
                Destroy(markerCollider);
            }

            Material markerMaterial = CreateUnlitMaterial(
                "Argus Orbit Marker",
                new Color(0.1f, 0.9f, 1.0f, 0.95f));
            marker.GetComponent<Renderer>().sharedMaterial = markerMaterial;
        }

        private static GameObject CreateCube(
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Material material)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = localPosition;
            cube.transform.localScale = localScale;
            cube.layer = SpacecraftModelLayer;
            cube.GetComponent<Renderer>().sharedMaterial = material;
            Collider collider = cube.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            return cube;
        }

        private static void CreateCylinder(
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Quaternion localRotation,
            Material material)
        {
            GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylinder.name = name;
            cylinder.transform.SetParent(parent, false);
            cylinder.transform.localPosition = localPosition;
            cylinder.transform.localScale = localScale;
            cylinder.transform.localRotation = localRotation;
            cylinder.layer = SpacecraftModelLayer;
            cylinder.GetComponent<Renderer>().sharedMaterial = material;
            Collider collider = cylinder.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
        }

        private static Material CreateMaterial(
            string name,
            Color color,
            float metallic,
            float smoothness)
        {
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
            Material material = new Material(shader) { name = name, color = color };
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            return material;
        }

        private static Material CreateUnlitMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            return new Material(shader) { name = name, color = color };
        }
    }
}
