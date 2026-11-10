using System;
using System.Collections;
using System.Collections.Generic;
using Argus.Simulation.Core;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // Gives the nadir ground-truth camera first claim on Cesium tile streaming.
    //
    // Cesium weighs every registered view equally, so priority is expressed by choosing which
    // views it loads for:
    // - A hidden look-ahead camera streams tiles where the nadir camera will be a few wall-clock
    //   seconds from now, at the rate the host's run advances (SimulationRunner.SimulationRate).
    // - After a jump (the first state of a run or subscription) only the nadir and look-ahead views
    //   are registered until they have loaded, then every other view is restored. The host owns the
    //   run, so this never holds it: the nadir view only loads first.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CubeSatCameraRig))]
    public sealed class NadirTilePriority : MonoBehaviour
    {
        [Header("Look-ahead")]
        [SerializeField, Min(0f)] private float lookaheadWallSeconds = 4f;
        [SerializeField, Min(0f)] private float maximumLookaheadSimulationSeconds = 600f;

        [Header("Warm-up")]
        [SerializeField] private bool warmUpOnStart = true;
        [SerializeField, Range(90f, 100f)] private float minimumLoadProgress = 100f;
        [SerializeField, Min(1)] private int stableFramesRequired = 10;
        [SerializeField, Min(1f)] private float warmUpTimeoutSeconds = 30f;

        private readonly List<Camera> _deferredCameras = new List<Camera>(4);

        private SimulationRunner _runner;
        private CubeSatCameraRig _cameraRig;
        private CesiumCameraManager _cameraManager;
        private Camera _lookaheadCamera;
        private CesiumGlobeAnchor _lookaheadAnchor;
        private Coroutine _warmUp;
        private bool _restoreMainCamera;
        private bool _restoreSceneViewCamera;

        public bool IsWarmingUp => _warmUp != null;
        public float WarmUpProgress { get; private set; }
        public Camera LookaheadCamera => _lookaheadCamera;

        public void Configure(SimulationRunner runner)
        {
            if (_runner != null)
            {
                _runner.HistoryReset -= RequestWarmUp;
            }

            _runner = runner;
            if (_runner != null && isActiveAndEnabled)
            {
                _runner.HistoryReset += RequestWarmUp;
            }
        }

        private void Start()
        {
            if (_runner == null)
            {
                Configure(FindAnyObjectByType<SimulationRunner>());
            }
            else
            {
                _runner.HistoryReset -= RequestWarmUp;
                _runner.HistoryReset += RequestWarmUp;
            }

            _cameraRig = GetComponent<CubeSatCameraRig>();
            _cameraRig.BuildIfNeeded();
            _cameraManager = CesiumCameraManager.GetOrCreate(gameObject);
            BuildLookaheadCamera();

            if (warmUpOnStart)
            {
                RequestWarmUp();
            }
        }

        // Loads the nadir view before every other view, from the next state on. Called on start and
        // on every jump in position (a new run or subscription).
        public void RequestWarmUp()
        {
            if (!isActiveAndEnabled || _runner == null || _cameraManager == null ||
                _cameraRig == null || _cameraRig.GroundTruthCamera == null)
            {
                return;
            }

            if (_warmUp != null)
            {
                StopCoroutine(_warmUp);
            }
            else
            {
                DeferNonNadirViews();
            }

            _warmUp = StartCoroutine(WarmUp());
        }

        private IEnumerator WarmUp()
        {
            // The nadir camera needs a pose to load for; the host may not be streaming yet.
            while (!_runner.HasState)
            {
                WarmUpProgress = 0f;
                yield return null;
            }

            Cesium3DTileset[] tilesets = FindObjectsByType<Cesium3DTileset>(
                FindObjectsInactive.Exclude);
            int requiredStableFrames = Math.Max(1, stableFramesRequired);
            float startedAt = Time.realtimeSinceStartup;
            int stableFrames = 0;

            while (stableFrames < requiredStableFrames)
            {
                // Guard against NaN while a tileset has not counted any tiles yet.
                float progress = NavigationEpisodeExporter.MinimumEnabledTilesetProgress(tilesets);
                WarmUpProgress = float.IsNaN(progress) ? 0f : progress;
                stableFrames = WarmUpProgress >= minimumLoadProgress
                    ? stableFrames + 1
                    : 0;

                if (Time.realtimeSinceStartup - startedAt >= warmUpTimeoutSeconds)
                {
                    Debug.LogWarning(
                        $"Nadir imagery reached {WarmUpProgress:0}% within " +
                        $"{warmUpTimeoutSeconds:0} seconds; continuing without it.",
                        this);
                    break;
                }

                yield return null;
            }

            _warmUp = null;
            RestoreDeferredViews();
        }

        private void LateUpdate()
        {
            if (_lookaheadAnchor == null || _runner == null || !_runner.HasState)
            {
                return;
            }

            double leadSeconds = Math.Min(
                lookaheadWallSeconds * Math.Max(0.0, _runner.SimulationRate),
                maximumLookaheadSimulationSeconds);
            Vector3d position = OrbitLookahead.PredictPositionEcef(_runner.LastState.Spacecraft, leadSeconds);
            _lookaheadAnchor.positionGlobeFixed = new double3(position.X, position.Y, position.Z);
            _lookaheadAnchor.rotationEastUpNorth = CubeSatCameraRig.NorthUpNadirEastUpNorth();
            _lookaheadAnchor.Sync();
        }

        private void BuildLookaheadCamera()
        {
            Camera groundTruth = _cameraRig.GroundTruthCamera;
            if (groundTruth == null)
            {
                Debug.LogWarning("Nadir tile priority needs the nadir ground-truth camera.", this);
                return;
            }

            GameObject cameraObject = new GameObject("Nadir Look-Ahead Camera");
            cameraObject.transform.SetParent(groundTruth.transform.parent, false);
            _lookaheadCamera = cameraObject.AddComponent<Camera>();
            _lookaheadCamera.CopyFrom(groundTruth);
            // Cesium uses disabled cameras for tile selection without rendering them. Sharing the
            // nadir target keeps its pixel size, and therefore its level of detail, identical.
            _lookaheadCamera.enabled = false;
            _lookaheadCamera.targetTexture = groundTruth.targetTexture;

            _lookaheadAnchor = cameraObject.AddComponent<CesiumGlobeAnchor>();
            _lookaheadAnchor.adjustOrientationForGlobeWhenMoving = false;
            _lookaheadAnchor.detectTransformChanges = false;

            _cameraManager.additionalCameras.Add(_lookaheadCamera);
        }

        private void DeferNonNadirViews()
        {
            _deferredCameras.Clear();
            Camera groundTruth = _cameraRig.GroundTruthCamera;
            foreach (Camera camera in _cameraRig.Cameras)
            {
                if (camera != groundTruth && _cameraManager.additionalCameras.Remove(camera))
                {
                    _deferredCameras.Add(camera);
                }
            }

            _restoreMainCamera = _cameraManager.useMainCamera;
            _restoreSceneViewCamera = _cameraManager.useSceneViewCameraInEditor;
            _cameraManager.useMainCamera = false;
            _cameraManager.useSceneViewCameraInEditor = false;
        }

        private void RestoreDeferredViews()
        {
            if (_cameraManager == null)
            {
                return;
            }

            foreach (Camera camera in _deferredCameras)
            {
                if (camera != null && !_cameraManager.additionalCameras.Contains(camera))
                {
                    _cameraManager.additionalCameras.Add(camera);
                }
            }

            _deferredCameras.Clear();
            _cameraManager.useMainCamera = _restoreMainCamera;
            _cameraManager.useSceneViewCameraInEditor = _restoreSceneViewCamera;
        }

        private void OnDisable()
        {
            if (_warmUp == null)
            {
                return;
            }

            // Unity has already stopped the coroutine.
            _warmUp = null;
            RestoreDeferredViews();
        }

        private void OnDestroy()
        {
            if (_runner != null)
            {
                _runner.HistoryReset -= RequestWarmUp;
            }

            if (_cameraManager != null && _lookaheadCamera != null)
            {
                _cameraManager.additionalCameras.Remove(_lookaheadCamera);
            }

            if (_lookaheadCamera != null)
            {
                Destroy(_lookaheadCamera.gameObject);
            }
        }
    }
}
