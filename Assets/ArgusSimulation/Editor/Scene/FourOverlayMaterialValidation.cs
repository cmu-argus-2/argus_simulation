using System;
using CesiumForUnity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Argus.Simulation.Editor
{
    // Offline check: a local fourth texture must change the rendered surface, not merely
    // exist as an unused shader property. The geographic GIBS screenshot is a separate check.
    public static class FourOverlayMaterialValidation
    {
        [MenuItem("Argus Simulation/Validate Four Overlay Material")]
        public static void Validate()
        {
            Material asset = AssetDatabase.LoadAssetAtPath<Material>(
                FoundationSceneBuilder.TilesetMaterialPath);
            if (asset == null || ShaderUtil.ShaderHasError(asset.shader))
            {
                throw new InvalidOperationException("Four-overlay material is missing or its shader has errors.");
            }

            for (int index = 0; index < 4; index++)
            {
                foreach (string prefix in new[]
                    { "_overlayTexture_", "_overlayTextureCoordinateIndex_", "_overlayTranslationAndScale_" })
                {
                    if (!asset.HasProperty(prefix + index))
                    {
                        throw new InvalidOperationException("Missing shader property: " + prefix + index);
                    }
                }
            }

            Scene preview = EditorSceneManager.NewPreviewScene();
            Material material = new Material(asset);
            Texture2D transparent = SolidTexture(Color.clear);
            Texture2D red = SolidTexture(Color.red);
            Texture2D green = SolidTexture(Color.green);
            RenderTexture target = new RenderTexture(32, 32, 24);
            RenderTexture previousTarget = RenderTexture.active;
            try
            {
                const int layer = 30;
                GameObject surface = GameObject.CreatePrimitive(PrimitiveType.Quad);
                SceneManager.MoveGameObjectToScene(surface, preview);
                surface.layer = layer;
                surface.GetComponent<Renderer>().sharedMaterial = material;

                GameObject lightObject = new GameObject("Validation light");
                SceneManager.MoveGameObjectToScene(lightObject, preview);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1;
                light.cullingMask = 1 << layer;

                GameObject cameraObject = new GameObject("Validation camera");
                SceneManager.MoveGameObjectToScene(cameraObject, preview);
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.scene = preview;
                camera.enabled = false;
                camera.transform.position = new Vector3(0, 0, -2);
                camera.orthographic = true;
                camera.orthographicSize = 0.6f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << layer;
                camera.targetTexture = target;

                for (int index = 0; index < 4; index++)
                {
                    material.SetTexture("_overlayTexture_" + index, transparent);
                    material.SetFloat("_overlayTextureCoordinateIndex_" + index, 0);
                    material.SetVector("_overlayTranslationAndScale_" + index, new Vector4(0, 0, 1, 1));
                }
                material.SetTexture("_overlayTexture_Clipping", transparent);
                material.SetTexture("_overlayTexture_0", red);
                Color before = Sample(camera, target);
                material.SetTexture("_overlayTexture_3", green);
                Color after = Sample(camera, target);
                if (before.r <= before.g + 0.1f || after.g <= after.r + 0.1f)
                {
                    throw new InvalidOperationException(
                        $"Fourth overlay did not replace the base color: before={before}, after={after}");
                }
                Debug.Log($"Four-overlay GPU validation passed: base={before}, slot 3={after}. " +
                    "This uses local textures; GIBS geographic acceptance still requires a screenshot.");
            }
            finally
            {
                RenderTexture.active = previousTarget;
                EditorSceneManager.ClosePreviewScene(preview);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(transparent);
                UnityEngine.Object.DestroyImmediate(red);
                UnityEngine.Object.DestroyImmediate(green);
            }
        }

        private static Texture2D SolidTexture(Color color)
        {
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.SetPixels(new[] { color, color, color, color });
            texture.Apply();
            return texture;
        }

        private static Color Sample(Camera camera, RenderTexture target)
        {
            camera.Render();
            RenderTexture.active = target;
            Texture2D image = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            try
            {
                image.ReadPixels(new Rect(16, 16, 1, 1), 0, 0);
                image.Apply();
                return image.GetPixel(0, 0);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }
}
