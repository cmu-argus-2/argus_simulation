using Argus.Simulation.Core;
using Argus.Simulation.Unity;
using CesiumForUnity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Argus.Simulation.Editor
{
    public static class FoundationSceneBuilder
    {
        private const string SceneDirectory = "Assets/ArgusSimulation/Scenes";
        private const string ScenePath = SceneDirectory + "/Foundation.unity";
        private const string PendingCreationKey = "ArgusSimulation.FoundationSceneCreationPending";

        [InitializeOnLoadMethod]
        private static void CreateFoundationSceneOnFirstImport()
        {
            EditorApplication.delayCall += EnsureFoundationSceneExists;
        }

        private static void EnsureFoundationSceneExists()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                AddFoundationSceneToBuildSettings();
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SessionState.SetBool(PendingCreationKey, true);
                EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
                EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
                EditorApplication.isPlaying = false;
                return;
            }

            CreateFoundationScene();
        }

        private static void HandlePlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode ||
                !SessionState.GetBool(PendingCreationKey, false))
            {
                return;
            }

            SessionState.SetBool(PendingCreationKey, false);
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            EditorApplication.delayCall += EnsureFoundationSceneExists;
        }

        [MenuItem("Argus Simulation/Create Foundation Scene")]
        public static void CreateFoundationScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject geospatialRoot = new GameObject("Geospatial World");
            geospatialRoot.AddComponent<CesiumGeoreference>();
            geospatialRoot.AddComponent<CesiumCameraManager>();

            GameObject earth = new GameObject("Cesium World Terrain + NASA GIBS");
            earth.transform.SetParent(geospatialRoot.transform, false);
            Cesium3DTileset tileset = earth.AddComponent<Cesium3DTileset>();
            tileset.tilesetSource = CesiumDataSource.FromCesiumIon;
            tileset.ionAssetID = 1;
            tileset.maximumScreenSpaceError = 4.0f;
            tileset.createPhysicsMeshes = false;
            tileset.enabled = false;
            CesiumIonEnvironmentLoader credentialLoader =
                earth.AddComponent<CesiumIonEnvironmentLoader>();
            credentialLoader.Configure(tileset);
            earth.AddComponent<NasaGibsRasterController>();

            GameObject simulation = new GameObject("Simulation");
            AnalyticOrbitStateSource orbitSource = simulation.AddComponent<AnalyticOrbitStateSource>();
            SimulationRunner runner = simulation.AddComponent<SimulationRunner>();
            runner.Configure(orbitSource);
            simulation.AddComponent<NavigationEpisodeExporter>();

            GameObject spacecraft = new GameObject("CubeSat Truth Pose");
            spacecraft.transform.SetParent(geospatialRoot.transform, false);
            CesiumGlobeAnchor spacecraftAnchor = spacecraft.AddComponent<CesiumGlobeAnchor>();
            CesiumSpacecraftPoseDriver poseDriver = spacecraft.AddComponent<CesiumSpacecraftPoseDriver>();
            poseDriver.Configure(runner, spacecraftAnchor);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "CubeSat Body (placeholder)";
            body.transform.SetParent(spacecraft.transform, false);
            body.transform.localScale = new Vector3(1f, 1f, 2f);

            GameObject sensorRig = new GameObject("Sensor Rig");
            sensorRig.transform.SetParent(spacecraft.transform, false);
            GameObject nadirCameraObject = new GameObject("Nadir Camera");
            nadirCameraObject.transform.SetParent(sensorRig.transform, false);
            Camera nadirCamera = nadirCameraObject.AddComponent<Camera>();
            nadirCamera.enabled = false;
            nadirCamera.fieldOfView = 45f;
            nadirCamera.nearClipPlane = 0.1f;
            nadirCamera.farClipPlane = 2_000_000f;

            GameObject observer = new GameObject("Globe Observer");
            observer.transform.SetParent(geospatialRoot.transform, false);
            CesiumGlobeAnchor observerAnchor = observer.AddComponent<CesiumGlobeAnchor>();
            observerAnchor.longitudeLatitudeHeight =
                new global::Unity.Mathematics.double3(-42.0, 18.0, 18_000_000.0);
            GameObject mainCameraObject = new GameObject("Main Camera");
            mainCameraObject.tag = "MainCamera";
            mainCameraObject.transform.SetParent(observer.transform, false);
            mainCameraObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Camera mainCamera = mainCameraObject.AddComponent<Camera>();
            mainCameraObject.AddComponent<RuntimeGlobeCameraController>();
            mainCamera.fieldOfView = 45f;
            mainCamera.nearClipPlane = 1_000f;
            mainCamera.farClipPlane = 40_000_000f;
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.backgroundColor = Color.black;

            GameObject sunlight = new GameObject("Sunlight");
            Light light = sunlight.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            sunlight.transform.rotation = Quaternion.Euler(30f, -35f, 0f);

            EnsureDirectory(SceneDirectory);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddFoundationSceneToBuildSettings();
            Selection.activeGameObject = earth;
            Debug.Log($"Created the Argus simulation foundation scene at {ScenePath}.");
        }

        private static void AddFoundationSceneToBuildSettings()
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            for (int index = 0; index < scenes.Length; index++)
            {
                if (scenes[index].path == ScenePath)
                {
                    if (!scenes[index].enabled)
                    {
                        scenes[index].enabled = true;
                        EditorBuildSettings.scenes = scenes;
                    }

                    return;
                }
            }

            EditorBuildSettingsScene[] updatedScenes =
                new EditorBuildSettingsScene[scenes.Length + 1];
            scenes.CopyTo(updatedScenes, 0);
            updatedScenes[scenes.Length] = new EditorBuildSettingsScene(ScenePath, true);
            EditorBuildSettings.scenes = updatedScenes;
        }

        private static void EnsureDirectory(string path)
        {
            string[] segments = path.Split('/');
            string current = segments[0];
            for (int index = 1; index < segments.Length; index++)
            {
                string next = current + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segments[index]);
                }

                current = next;
            }
        }
    }
}
