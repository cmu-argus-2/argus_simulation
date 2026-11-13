using Argus.Simulation.Core;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    // Draws the track the spacecraft has flown, from the states received from the host: the run
    // cannot be sampled ahead in Unity (G2). It keeps one Earth-fixed position per
    // sampleSpacingSeconds of simulated time, the last sampleCount of them (about 100 minutes, an
    // orbit, by default), and clears them on a new run. A predicted track sent by the host is
    // UNITY-8 (#62).
    [DisallowMultipleComponent]
    public sealed class OrbitTrailRenderer : MonoBehaviour
    {
        [SerializeField] private SimulationRunner runner;
        [SerializeField, Range(64, 2048)] private int sampleCount = 600;
        [SerializeField, Min(0.1f)] private float sampleSpacingSeconds = 10f;
        [SerializeField] private float trailWidthMeters = 28_000f;

        private CesiumGeoreference _georeference;
        private LineRenderer _lineRenderer;
        private Vector3d[] _positionsEcef;
        private int _count;
        private int _next;
        private double _lastSampleSeconds;
        private bool _isDirty;

        public int PointCount => _count;

        public void Configure(SimulationRunner simulationRunner)
        {
            if (isActiveAndEnabled)
            {
                Unsubscribe();
            }

            runner = simulationRunner;
            if (isActiveAndEnabled)
            {
                Subscribe();
            }
        }

        private void OnEnable()
        {
            if (runner == null)
            {
                runner = FindAnyObjectByType<SimulationRunner>();
            }

            _positionsEcef = new Vector3d[sampleCount];
            ClearTrack();
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        // Re-projects every point each frame the track changes, since the georeference origin can move.
        private void LateUpdate()
        {
            if (!_isDirty)
            {
                return;
            }

            if (_georeference == null)
            {
                _georeference = FindAnyObjectByType<CesiumGeoreference>();
                if (_georeference == null)
                {
                    return;
                }
            }

            EnsureLineRenderer();
            _lineRenderer.positionCount = _count;
            int oldest = _count < _positionsEcef.Length ? 0 : _next;
            for (int index = 0; index < _count; index++)
            {
                Vector3d ecef = _positionsEcef[(oldest + index) % _positionsEcef.Length];
                double3 unity = _georeference.TransformEarthCenteredEarthFixedPositionToUnity(
                    new double3(ecef.X, ecef.Y, ecef.Z));
                _lineRenderer.SetPosition(index, new Vector3((float)unity.x, (float)unity.y, (float)unity.z));
            }

            _isDirty = false;
        }

        private void HandleState(SimulationState state)
        {
            double time = state.Spacecraft.SimulationTimeSeconds;
            if (_count > 0 && time - _lastSampleSeconds < sampleSpacingSeconds)
            {
                return;
            }

            _lastSampleSeconds = time;
            _positionsEcef[_next] = state.Spacecraft.PositionEcefMeters;
            _next = (_next + 1) % _positionsEcef.Length;
            _count = Mathf.Min(_count + 1, _positionsEcef.Length);
            _isDirty = true;
        }

        private void ClearTrack()
        {
            _count = 0;
            _next = 0;
            _isDirty = true;
        }

        private void Subscribe()
        {
            if (runner != null)
            {
                runner.StateProduced += HandleState;
                runner.HistoryReset += ClearTrack;
            }
        }

        private void Unsubscribe()
        {
            if (runner != null)
            {
                runner.StateProduced -= HandleState;
                runner.HistoryReset -= ClearTrack;
            }
        }

        private void EnsureLineRenderer()
        {
            if (_lineRenderer != null)
            {
                return;
            }

            _lineRenderer = gameObject.AddComponent<LineRenderer>();
            _lineRenderer.name = "Orbit Ground Track";
            _lineRenderer.useWorldSpace = true;
            _lineRenderer.loop = false;
            _lineRenderer.widthMultiplier = trailWidthMeters;
            _lineRenderer.numCornerVertices = 2;
            _lineRenderer.numCapVertices = 2;
            _lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _lineRenderer.receiveShadows = false;
            _lineRenderer.gameObject.layer = CubeSatVisualModel.OrbitMarkerLayer;

            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            Material material = new Material(shader)
            {
                name = "Argus Orbit Trail",
                color = new Color(0.05f, 0.75f, 1.0f, 0.72f)
            };
            _lineRenderer.sharedMaterial = material;
        }
    }
}
