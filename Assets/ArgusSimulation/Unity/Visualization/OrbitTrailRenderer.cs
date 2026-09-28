using System.Collections.Generic;
using Argus.Simulation.Core;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;

namespace Argus.Simulation.Unity
{
    [DisallowMultipleComponent]
    public sealed class OrbitTrailRenderer : MonoBehaviour
    {
        [SerializeField] private SimulationRunner runner;
        [SerializeField, Range(64, 512)] private int sampleCount = 240;
        [SerializeField] private float trailWidthMeters = 28_000f;

        private CesiumGeoreference _georeference;
        private LineRenderer _lineRenderer;

        public void Configure(SimulationRunner simulationRunner)
        {
            runner = simulationRunner;
        }

        private void Start()
        {
            _georeference = FindAnyObjectByType<CesiumGeoreference>();
            BuildTrail();
        }

        public void BuildTrail()
        {
            if (runner == null)
            {
                runner = FindAnyObjectByType<SimulationRunner>();
            }

            if (_georeference == null)
            {
                _georeference = FindAnyObjectByType<CesiumGeoreference>();
            }

            if (runner == null || runner.StateSource == null || _georeference == null)
            {
                return;
            }

            _georeference.Initialize();
            EnsureLineRenderer();
            double periodSeconds = runner.StateSource is AnalyticOrbitStateSource analytic
                ? analytic.EstimatedPeriodSeconds
                : 5_700.0;

            // Times without a state (e.g. a backend/runtime failure) are skipped, not drawn at the origin.
            List<Vector3> positions = new List<Vector3>(sampleCount);
            for (int index = 0; index < sampleCount; index++)
            {
                double time = periodSeconds * index / (sampleCount - 1.0);
                if (!runner.StateSource.TryGetState(index, time, out SpacecraftState state))
                {
                    continue;
                }

                Vector3d ecef = state.PositionEcefMeters;
                double3 unity = _georeference.TransformEarthCenteredEarthFixedPositionToUnity(
                    new double3(ecef.X, ecef.Y, ecef.Z));
                positions.Add(new Vector3((float)unity.x, (float)unity.y, (float)unity.z));
            }

            _lineRenderer.positionCount = positions.Count;
            _lineRenderer.SetPositions(positions.ToArray());
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
