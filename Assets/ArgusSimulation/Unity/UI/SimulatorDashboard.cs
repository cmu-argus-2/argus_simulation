using System;
using System.Collections.Generic;
using System.Text;
using Argus.Simulation.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Argus.Simulation.Unity
{
    [DisallowMultipleComponent]
    public sealed class SimulatorDashboard : MonoBehaviour
    {
        private enum SensorGroup
        {
            OrbitTruth,
            BodyRate,
            Environment,
            Cameras,
            SensorAvailability
        }

        private static readonly Color PanelColor = new Color(0.025f, 0.043f, 0.067f, 0.96f);
        private static readonly Color RaisedPanelColor = new Color(0.045f, 0.075f, 0.105f, 0.98f);
        private static readonly Color AccentColor = new Color(0.12f, 0.82f, 0.95f, 1f);
        private static readonly Color TextColor = new Color(0.86f, 0.92f, 0.96f, 1f);
        private static readonly Color MutedTextColor = new Color(0.55f, 0.66f, 0.72f, 1f);

        private readonly Dictionary<SensorGroup, bool> _visibleGroups =
            new Dictionary<SensorGroup, bool>();
        private readonly double[] _speedOptions = { 1.0, 10.0, 60.0 };

        private SimulationRunner _runner;
        private AnalyticOrbitStateSource _orbitSource;
        private SimulationSensorRuntime _sensorRuntime;
        private CubeSatCameraRig _cameraRig;
        private NavigationEpisodeExporter _episodeExporter;
        private Camera _mainCamera;
        private Font _font;
        private Text _telemetryText;
        private Text _statusText;
        private Text _pauseButtonText;
        private Text _speedButtonText;
        private Text _captureButtonText;
        private Text _poseStatusText;
        private GameObject _settingsPanel;
        private InputField _gtDateField;
        private InputField _gtLayerField;
        private Text _settingsStatusText;
        private float _nextTelemetryRefresh;
        private int _speedIndex;
        private double _initialAltitudeMeters;
        private double _initialPhaseDegrees;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateForFoundationScene()
        {
            if (FindAnyObjectByType<SimulationRunner>() == null ||
                FindAnyObjectByType<SimulatorDashboard>() != null)
            {
                return;
            }

            new GameObject("Argus Simulator Dashboard").AddComponent<SimulatorDashboard>();
        }

        private void Awake()
        {
            _runner = FindAnyObjectByType<SimulationRunner>();
            _mainCamera = Camera.main;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            foreach (SensorGroup group in Enum.GetValues(typeof(SensorGroup)))
            {
                _visibleGroups[group] = true;
            }

            EnsureEventSystem();
            BuildSpacecraftSystems();
            ConfigureMainCamera();
            BuildInterface();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextTelemetryRefresh)
            {
                return;
            }

            _nextTelemetryRefresh = Time.unscaledTime + 0.2f;
            RefreshTelemetry();
        }

        private void BuildSpacecraftSystems()
        {
            if (_runner == null)
            {
                return;
            }

            _orbitSource = _runner.StateSource as AnalyticOrbitStateSource;
            if (_orbitSource != null)
            {
                _initialAltitudeMeters = _orbitSource.AltitudeMeters;
                _initialPhaseDegrees = _orbitSource.PhaseDegrees;
            }

            _sensorRuntime = _runner.GetComponent<SimulationSensorRuntime>();
            if (_sensorRuntime == null)
            {
                _sensorRuntime = _runner.gameObject.AddComponent<SimulationSensorRuntime>();
            }
            _sensorRuntime.Configure(_runner);

            _episodeExporter = _runner.GetComponent<NavigationEpisodeExporter>();
            if (_episodeExporter == null)
            {
                _episodeExporter = _runner.gameObject.AddComponent<NavigationEpisodeExporter>();
            }
            _episodeExporter.CaptureStatusChanged += HandleCaptureStatusChanged;

            GameObject spacecraft = GameObject.Find("CubeSat Truth Pose");
            if (spacecraft != null)
            {
                CubeSatVisualModel visual = spacecraft.GetComponent<CubeSatVisualModel>();
                if (visual == null)
                {
                    visual = spacecraft.AddComponent<CubeSatVisualModel>();
                }
                visual.BuildIfNeeded();

                _cameraRig = spacecraft.GetComponent<CubeSatCameraRig>();
                if (_cameraRig == null)
                {
                    _cameraRig = spacecraft.AddComponent<CubeSatCameraRig>();
                }
                _cameraRig.BuildIfNeeded();
            }

            OrbitTrailRenderer trail = FindAnyObjectByType<OrbitTrailRenderer>();
            if (trail == null)
            {
                GameObject trailObject = new GameObject("Orbit Visualization");
                trail = trailObject.AddComponent<OrbitTrailRenderer>();
            }
            trail.Configure(_runner);

            Light sunlight = FindSunlight();
            if (sunlight != null)
            {
                SunLightDriver sunDriver = sunlight.GetComponent<SunLightDriver>();
                if (sunDriver == null)
                {
                    sunDriver = sunlight.gameObject.AddComponent<SunLightDriver>();
                }
                sunDriver.Configure(_runner);
            }
        }

        private static Light FindSunlight()
        {
            GameObject sunlightObject = GameObject.Find("Sunlight");
            Light light = sunlightObject != null ? sunlightObject.GetComponent<Light>() : FindAnyObjectByType<Light>();
            return light != null && light.type == LightType.Directional ? light : null;
        }

        private static string DescribeSunlight(SunObservation observation)
        {
            switch (observation.Condition)
            {
                case SunlightCondition.Sunlit:
                    return "SUNLIT";
                case SunlightCondition.Penumbra:
                    return $"PENUMBRA {observation.IlluminationFraction * 100.0:F0}%";
                default:
                    return "UMBRA (ECLIPSE)";
            }
        }

        private void ConfigureMainCamera()
        {
            if (_mainCamera == null)
            {
                return;
            }

            _mainCamera.rect = new Rect(0f, 0.30f, 0.70f, 0.64f);
            _mainCamera.cullingMask |= 1 << CubeSatVisualModel.OrbitMarkerLayer;
            _mainCamera.cullingMask &= ~(1 << CubeSatVisualModel.SpacecraftModelLayer);
        }

        private void BuildInterface()
        {
            GameObject canvasObject = new GameObject("Mission Control UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(2560f, 1440f);
            scaler.matchWidthOrHeight = 0.5f;

            RectTransform canvasTransform = canvasObject.GetComponent<RectTransform>();
            BuildTopBar(canvasTransform);
            BuildMissionViewLabel(canvasTransform);
            BuildPoseControlPanel(canvasTransform);
            BuildCameraFeedStrip(canvasTransform);
            BuildTelemetryPanel(canvasTransform);
            BuildSettingsPanel(canvasTransform);
        }

        private void BuildTopBar(RectTransform parent)
        {
            RectTransform topBar = CreatePanel(
                "Top Toolbar",
                parent,
                new Vector2(0f, 0.94f),
                Vector2.one,
                PanelColor);

            CreateText(
                "Title",
                topBar,
                "ARGUS  /  CUBESAT SIMULATION CONTROL",
                28,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                AccentColor,
                new Vector2(0.018f, 0f),
                new Vector2(0.36f, 1f));

            _statusText = CreateText(
                "Status",
                topBar,
                "● INITIALIZING",
                20,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                new Color(0.98f, 0.72f, 0.22f),
                new Vector2(0.37f, 0f),
                new Vector2(0.46f, 1f));

            Button settingsButton = CreateButton(
                "Settings Button",
                topBar,
                "SETTINGS",
                new Vector2(0.47f, 0.18f),
                new Vector2(0.54f, 0.82f));
            settingsButton.GetComponentInChildren<Text>().fontSize = 15;
            settingsButton.onClick.AddListener(ToggleSettings);

            Button captureButton = CreateButton(
                "Capture Button",
                topBar,
                "CAPTURE",
                new Vector2(0.55f, 0.18f),
                new Vector2(0.66f, 0.82f));
            _captureButtonText = captureButton.GetComponentInChildren<Text>();
            _captureButtonText.fontSize = 15;
            captureButton.onClick.AddListener(CaptureImages);

            Button pauseButton = CreateButton(
                "Pause Button",
                topBar,
                "PAUSE",
                new Vector2(0.67f, 0.18f),
                new Vector2(0.76f, 0.82f));
            _pauseButtonText = pauseButton.GetComponentInChildren<Text>();
            pauseButton.onClick.AddListener(TogglePause);

            Button resetButton = CreateButton(
                "Reset Button",
                topBar,
                "RESET",
                new Vector2(0.77f, 0.18f),
                new Vector2(0.85f, 0.82f));
            resetButton.onClick.AddListener(ResetSimulation);

            Button speedButton = CreateButton(
                "Speed Button",
                topBar,
                "TIME 1×",
                new Vector2(0.86f, 0.18f),
                new Vector2(0.94f, 0.82f));
            _speedButtonText = speedButton.GetComponentInChildren<Text>();
            speedButton.onClick.AddListener(CycleSpeed);

            CreateText(
                "Source Badge",
                topBar,
                "ANALYTIC ORBIT",
                17,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                MutedTextColor,
                new Vector2(0.945f, 0f),
                new Vector2(0.998f, 1f));
        }

        private void BuildMissionViewLabel(RectTransform parent)
        {
            RectTransform label = CreatePanel(
                "Mission View Label",
                parent,
                new Vector2(0.012f, 0.895f),
                new Vector2(0.23f, 0.932f),
                new Color(0.02f, 0.04f, 0.06f, 0.82f));
            CreateText(
                "Mission View Text",
                label,
                "GLOBAL MISSION VIEW  •  CESIUM / NASA GIBS",
                18,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor,
                Vector2.zero,
                Vector2.one);
        }

        private void BuildPoseControlPanel(RectTransform parent)
        {
            RectTransform panel = CreatePanel(
                "Pose Controls",
                parent,
                new Vector2(0.012f, 0.805f),
                new Vector2(0.688f, 0.888f),
                new Color(0.02f, 0.04f, 0.06f, 0.90f));

            _poseStatusText = CreateText(
                "Pose Status",
                panel,
                "POSE CONTROL",
                14,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                AccentColor,
                new Vector2(0.012f, 0.05f),
                new Vector2(0.18f, 0.95f));

            AddPoseButton(panel, "PHASE -15°", 0.19f, 0.53f, 0.34f, 0.94f,
                () => NudgeOrbit(-15.0, 0.0));
            AddPoseButton(panel, "PHASE +15°", 0.35f, 0.53f, 0.50f, 0.94f,
                () => NudgeOrbit(15.0, 0.0));
            AddPoseButton(panel, "ALT -50 KM", 0.51f, 0.53f, 0.66f, 0.94f,
                () => NudgeOrbit(0.0, -50_000.0));
            AddPoseButton(panel, "ALT +50 KM", 0.67f, 0.53f, 0.82f, 0.94f,
                () => NudgeOrbit(0.0, 50_000.0));
            AddPoseButton(panel, "RESET POSE", 0.83f, 0.53f, 0.988f, 0.94f,
                ResetPoseControls);

            AddPoseButton(panel, "PITCH -10°", 0.19f, 0.06f, 0.315f, 0.47f,
                () => NudgeAttitude(new Vector3(-10f, 0f, 0f)));
            AddPoseButton(panel, "PITCH +10°", 0.325f, 0.06f, 0.45f, 0.47f,
                () => NudgeAttitude(new Vector3(10f, 0f, 0f)));
            AddPoseButton(panel, "YAW -10°", 0.46f, 0.06f, 0.585f, 0.47f,
                () => NudgeAttitude(new Vector3(0f, -10f, 0f)));
            AddPoseButton(panel, "YAW +10°", 0.595f, 0.06f, 0.72f, 0.47f,
                () => NudgeAttitude(new Vector3(0f, 10f, 0f)));
            AddPoseButton(panel, "ROLL -10°", 0.73f, 0.06f, 0.855f, 0.47f,
                () => NudgeAttitude(new Vector3(0f, 0f, -10f)));
            AddPoseButton(panel, "ROLL +10°", 0.865f, 0.06f, 0.988f, 0.47f,
                () => NudgeAttitude(new Vector3(0f, 0f, 10f)));

            UpdatePoseStatus();
        }

        private void AddPoseButton(
            RectTransform parent,
            string label,
            float left,
            float bottom,
            float right,
            float top,
            Action action)
        {
            Button button = CreateButton(
                label + " Button",
                parent,
                label,
                new Vector2(left, bottom),
                new Vector2(right, top));
            button.GetComponentInChildren<Text>().fontSize = 13;
            button.onClick.AddListener(() => action());
        }

        private void BuildCameraFeedStrip(RectTransform parent)
        {
            RectTransform strip = CreatePanel(
                "Camera Feed Strip",
                parent,
                Vector2.zero,
                new Vector2(0.70f, 0.30f),
                PanelColor);

            CreateText(
                "Camera Header",
                strip,
                "1U CAMERA ARRAY  /  NADIR GT",
                17,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                AccentColor,
                new Vector2(0.012f, 0.91f),
                new Vector2(0.50f, 1f));

            if (_cameraRig == null)
            {
                return;
            }
            int previewCameraCount = _cameraRig.RenderTextures.Count;
            for (int index = 0; index < previewCameraCount; index++)
            {
                float columnWidth = 1f / previewCameraCount;
                float left = index * columnWidth + 0.004f;
                float right = (index + 1) * columnWidth - 0.004f;
                RectTransform feedPanel = CreatePanel(
                    "Feed " + index,
                    strip,
                    new Vector2(left, 0.025f),
                    new Vector2(right, 0.89f),
                    RaisedPanelColor);

                RawImage image = CreateUiObject<RawImage>("Image", feedPanel);
                image.texture = _cameraRig.RenderTextures[index];
                image.color = Color.white;
                image.raycastTarget = false;
                SetAnchors(image.rectTransform, new Vector2(0.02f, 0.04f), new Vector2(0.98f, 0.84f));
                AspectRatioFitter fitter = image.gameObject.AddComponent<AspectRatioFitter>();
                fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                fitter.aspectRatio = 16f / 9f;

                CreateText(
                    "Feed Label",
                    feedPanel,
                    $"CAM {index + 1}  /  {_cameraRig.Names[index]}",
                    15,
                    FontStyle.Bold,
                    TextAnchor.MiddleLeft,
                    TextColor,
                    new Vector2(0.035f, 0.84f),
                    new Vector2(0.98f, 0.99f));
            }
        }

        private void BuildTelemetryPanel(RectTransform parent)
        {
            RectTransform sidePanel = CreatePanel(
                "Telemetry Panel",
                parent,
                new Vector2(0.70f, 0f),
                new Vector2(1f, 0.94f),
                PanelColor);

            CreateText(
                "Telemetry Title",
                sidePanel,
                "TRUTH & SENSOR STATUS",
                23,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                AccentColor,
                new Vector2(0.05f, 0.955f),
                new Vector2(0.96f, 0.997f));

            CreateText(
                "Visibility Label",
                sidePanel,
                "VISIBLE DATA GROUPS",
                15,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                MutedTextColor,
                new Vector2(0.05f, 0.905f),
                new Vector2(0.96f, 0.945f));

            string[] labels =
            {
                "ORBIT TRUTH", "BODY RATE", "ENVIRONMENT", "CAMERAS", "AVAILABILITY"
            };
            SensorGroup[] groups = (SensorGroup[])Enum.GetValues(typeof(SensorGroup));
            for (int index = 0; index < groups.Length; index++)
            {
                int row = index / 2;
                int column = index % 2;
                float left = 0.05f + column * 0.47f;
                float top = 0.90f - row * 0.052f;
                SensorGroup capturedGroup = groups[index];
                Toggle toggle = CreateToggle(
                    "Toggle " + labels[index],
                    sidePanel,
                    labels[index],
                    new Vector2(left, top - 0.045f),
                    new Vector2(left + 0.44f, top));
                toggle.isOn = true;
                toggle.onValueChanged.AddListener(value => _visibleGroups[capturedGroup] = value);
            }

            RectTransform viewport = CreatePanel(
                "Telemetry Viewport",
                sidePanel,
                new Vector2(0.035f, 0.025f),
                new Vector2(0.97f, 0.765f),
                RaisedPanelColor);
            viewport.gameObject.AddComponent<RectMask2D>();

            ScrollRect scroll = viewport.gameObject.AddComponent<ScrollRect>();
            RectTransform content = CreateRectTransform("Telemetry Content", viewport);
            SetAnchors(content, new Vector2(0f, 1f), new Vector2(1f, 1f));
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 1500f);
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 28f;

            _telemetryText = CreateText(
                "Telemetry Text",
                content,
                "Waiting for spacecraft state…",
                17,
                FontStyle.Normal,
                TextAnchor.UpperLeft,
                TextColor,
                Vector2.zero,
                Vector2.one);
            _telemetryText.rectTransform.offsetMin = new Vector2(20f, 18f);
            _telemetryText.rectTransform.offsetMax = new Vector2(-18f, -18f);
            _telemetryText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _telemetryText.verticalOverflow = VerticalWrapMode.Overflow;
            ContentSizeFitter fitter = _telemetryText.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private void BuildSettingsPanel(RectTransform parent)
        {
            RectTransform panel = CreatePanel(
                "Settings Panel",
                parent,
                new Vector2(0.20f, 0.36f),
                new Vector2(0.50f, 0.78f),
                RaisedPanelColor);
            _settingsPanel = panel.gameObject;

            CreateText(
                "Settings Title",
                panel,
                "SETTINGS  /  GT IMAGERY",
                22,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                AccentColor,
                new Vector2(0.05f, 0.86f),
                new Vector2(0.95f, 0.97f));

            CreateText(
                "GT Date Label",
                panel,
                "NASA GIBS DATE (UTC, YYYY-MM-DD)",
                15,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                MutedTextColor,
                new Vector2(0.05f, 0.74f),
                new Vector2(0.95f, 0.83f));
            _gtDateField = CreateInputField(
                "GT Date Field",
                panel,
                NasaGibsRasterController.DefaultObservationDateUtc,
                new Vector2(0.05f, 0.62f),
                new Vector2(0.95f, 0.74f));

            CreateText(
                "GT Layer Label",
                panel,
                "NASA GIBS LAYER",
                15,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                MutedTextColor,
                new Vector2(0.05f, 0.49f),
                new Vector2(0.95f, 0.58f));
            _gtLayerField = CreateInputField(
                "GT Layer Field",
                panel,
                NasaGibsUrl.DefaultLayer,
                new Vector2(0.05f, 0.37f),
                new Vector2(0.95f, 0.49f));

            _settingsStatusText = CreateText(
                "Settings Status",
                panel,
                string.Empty,
                14,
                FontStyle.Normal,
                TextAnchor.UpperLeft,
                MutedTextColor,
                new Vector2(0.05f, 0.17f),
                new Vector2(0.95f, 0.34f));
            _settingsStatusText.horizontalOverflow = HorizontalWrapMode.Wrap;

            Button applyButton = CreateButton(
                "Apply Settings Button",
                panel,
                "APPLY",
                new Vector2(0.05f, 0.04f),
                new Vector2(0.48f, 0.15f));
            applyButton.onClick.AddListener(ApplyImagerySettings);

            Button closeButton = CreateButton(
                "Close Settings Button",
                panel,
                "CLOSE",
                new Vector2(0.52f, 0.04f),
                new Vector2(0.95f, 0.15f));
            closeButton.onClick.AddListener(ToggleSettings);

            _settingsPanel.SetActive(false);
        }

        private void ToggleSettings()
        {
            bool opening = !_settingsPanel.activeSelf;
            _settingsPanel.SetActive(opening);
            if (!opening)
            {
                return;
            }

            _settingsStatusText.color = MutedTextColor;

            NasaGibsRasterController imagery = FindAnyObjectByType<NasaGibsRasterController>();
            if (imagery == null)
            {
                _settingsStatusText.text = "No NASA GIBS imagery controller in this scene.";
                return;
            }

            _gtDateField.text = imagery.ObservationDateUtc;
            _gtLayerField.text = imagery.Layer;
            _settingsStatusText.text =
                $"Launch option: {NasaGibsRasterController.DateCommandLineOption} YYYY-MM-DD " +
                $"{NasaGibsRasterController.LayerCommandLineOption} LAYER";
        }

        private void ApplyImagerySettings()
        {
            NasaGibsRasterController imagery = FindAnyObjectByType<NasaGibsRasterController>();
            if (imagery == null)
            {
                _settingsStatusText.text = "No NASA GIBS imagery controller in this scene.";
                return;
            }

            if (imagery.TryConfigure(_gtLayerField.text, _gtDateField.text, out string error))
            {
                _settingsStatusText.text = $"Loading {imagery.Layer} for {imagery.ObservationDateUtc}.";
                _settingsStatusText.color = new Color(0.3f, 1f, 0.58f);
            }
            else
            {
                _settingsStatusText.text = error;
                _settingsStatusText.color = new Color(1f, 0.45f, 0.4f);
            }
        }

        private void RefreshTelemetry()
        {
            UpdatePoseStatus();

            if (_runner == null || !_runner.HasState)
            {
                _statusText.text = "● WAITING FOR STATE";
                _statusText.color = new Color(0.98f, 0.72f, 0.22f);
                _telemetryText.text = "Waiting for spacecraft state…";
                return;
            }

            _statusText.text = _runner.IsRunning ? "● SIMULATION RUNNING" : "● SIMULATION PAUSED";
            _statusText.color = _runner.IsRunning
                ? new Color(0.3f, 1f, 0.58f)
                : new Color(1f, 0.72f, 0.24f);
            _pauseButtonText.text = _runner.IsRunning ? "PAUSE" : "RESUME";

            SpacecraftState state = _runner.LastState;
            StringBuilder builder = new StringBuilder(1400);

            if (IsGroupVisible(SensorGroup.OrbitTruth))
            {
                AddHeader(builder, "SPACECRAFT TRUTH");
                AddRow(builder, "UTC", state.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                AddRow(builder, "Simulation time", $"{state.SimulationTimeSeconds,10:F1} s");
                AddRow(builder, "Sequence", state.Sequence.ToString());
                AddRow(builder, "Earth orientation", _orbitSource != null && _orbitSource.Ephemeris != null
                    ? "SPICE J2000→ITRF93"
                    : "ANALYTIC CONSTANT-SPIN FALLBACK");
                AddVector(builder, "ECEF position km", state.PositionEcefMeters / 1000.0);
                AddVector(builder, "ECEF velocity m/s", state.VelocityEcefMetersPerSecond);
                AddRow(builder, "Speed", $"{state.VelocityEcefMetersPerSecond.Magnitude / 1000.0:F3} km/s");
                Quaterniond q = state.BodyToEcef;
                AddRow(builder, "Body→ECEF q", $"{q.X:F4}, {q.Y:F4}, {q.Z:F4}, {q.W:F4}");
            }

            if (IsGroupVisible(SensorGroup.BodyRate))
            {
                AddHeader(builder, "BODY-RATE SENSOR");
                if (_sensorRuntime != null &&
                    _sensorRuntime.HasOutput &&
                    _sensorRuntime.Latest.TryGetFrame(
                        SimulationSensorRuntime.BodyRateSensorId,
                        out SensorFrame<AngularRateMeasurement> bodyRate))
                {
                    AddRow(builder, "Status", bodyRate.Status.ToString().ToUpperInvariant());
                    AddRow(builder, "Source", bodyRate.Source);
                    AddRow(builder, "Frame", bodyRate.FrameId);
                    AddRow(builder, "Sensor sequence", bodyRate.Sequence.ToString());
                    if (bodyRate.Status == SensorFrameStatus.Valid)
                    {
                        AddVector(
                            builder,
                            "Angular rate rad/s",
                            bodyRate.Payload.AngularVelocitySensorRadiansPerSecond);
                    }
                }
                else
                {
                    AddRow(builder, "Status", "UNAVAILABLE — no frame produced");
                }
            }

            if (IsGroupVisible(SensorGroup.Environment))
            {
                AddHeader(builder, "SPICE ENVIRONMENT TRUTH");
                if (_orbitSource != null &&
                    _orbitSource.TryGetSunObservation(state, out SunObservation sun))
                {
                    AddRow(builder, "Solar state", DescribeSunlight(sun));
                    AddRow(builder, "Illumination", $"{sun.IlluminationFraction * 100.0:F1}%");
                    AddVector(builder, "Sun direction body", sun.SunDirectionBody);
                    AddVector(builder, "Sun direction ITRF93", sun.SunDirectionItrf93);
                }
                else
                {
                    AddRow(builder, "Status", "UNAVAILABLE — no SPICE sample");
                }
            }

            if (IsGroupVisible(SensorGroup.Cameras))
            {
                AddHeader(builder, "CAMERA / PAYLOAD STATUS");
                if (_cameraRig == null || _cameraRig.Cameras.Count == 0)
                {
                    AddRow(builder, "Status", "UNAVAILABLE — no camera rig");
                }
                else
                {
                    for (int index = 0; index < _cameraRig.Cameras.Count; index++)
                    {
                        Camera camera = _cameraRig.Cameras[index];
                        string resolution = camera.targetTexture == null
                            ? "no target"
                            : $"{camera.targetTexture.width}×{camera.targetTexture.height}";
                        string status = camera.enabled ? "ACTIVE" : "DISABLED";
                        AddRow(
                            builder,
                            _cameraRig.Names[index],
                            $"{status} / {camera.fieldOfView:F1}° VFOV / {resolution}");
                    }
                }
            }

            if (IsGroupVisible(SensorGroup.SensorAvailability))
            {
                AddHeader(builder, "UNIMPLEMENTED SENSOR MODELS");
                AddRow(builder, "GNSS", "UNAVAILABLE — no model registered");
                AddRow(builder, "Accelerometer", "UNAVAILABLE — no model registered");
                AddRow(builder, "Magnetometer", "UNAVAILABLE — no model registered");
                AddRow(builder, "Sun sensor", "UNAVAILABLE — no model registered");
                AddRow(builder, "Star tracker", "UNAVAILABLE — no model registered");
                AddRow(builder, "Power / thermal", "UNAVAILABLE — no model registered");
                AddRow(builder, "Radio / radiation", "UNAVAILABLE — no model registered");
            }

            _telemetryText.text = builder.ToString();
        }

        private void NudgeOrbit(double phaseDeltaDegrees, double altitudeDeltaMeters)
        {
            if (_orbitSource == null)
            {
                return;
            }

            _orbitSource.PhaseDegrees += phaseDeltaDegrees;
            _orbitSource.AltitudeMeters += altitudeDeltaMeters;
            ProduceManualState();
        }

        private void NudgeAttitude(Vector3 deltaDegrees)
        {
            if (_runner == null)
            {
                return;
            }

            Quaternion delta = Quaternion.Euler(deltaDegrees);
            AttitudeOverrideCommand command = AttitudeOverrideCommand.ApplyDelta(
                _runner.NextSequence,
                _runner.SimulationTimeSeconds,
                new Quaterniond(delta.x, delta.y, delta.z, delta.w).Normalized());
            if (_runner.TryApplyAttitudeOverride(command))
            {
                ProduceManualState();
            }
        }

        private void ResetPoseControls()
        {
            if (_orbitSource != null)
            {
                _orbitSource.PhaseDegrees = _initialPhaseDegrees;
                _orbitSource.AltitudeMeters = _initialAltitudeMeters;
            }

            if (_runner != null)
            {
                _runner.TryApplyAttitudeOverride(AttitudeOverrideCommand.Clear(
                    _runner.NextSequence,
                    _runner.SimulationTimeSeconds));
            }
            ProduceManualState();
        }

        private void ProduceManualState()
        {
            if (_runner != null)
            {
                _runner.StepOnce();
            }

            UpdatePoseStatus();
            RefreshTelemetry();
        }

        private void UpdatePoseStatus()
        {
            if (_poseStatusText == null)
            {
                return;
            }

            if (_orbitSource == null || _runner == null || !_runner.SupportsAttitudeOverride)
            {
                _poseStatusText.text = "POSE CONTROL\nUNAVAILABLE";
                return;
            }

            Quaterniond overrideBody = _runner.AttitudeOverrideBody;
            Vector3 attitude = new Quaternion(
                (float)overrideBody.X,
                (float)overrideBody.Y,
                (float)overrideBody.Z,
                (float)overrideBody.W).eulerAngles;
            attitude = new Vector3(
                NormalizeSignedDegrees(attitude.x),
                NormalizeSignedDegrees(attitude.y),
                NormalizeSignedDegrees(attitude.z));
            _poseStatusText.text =
                $"ANALYTIC STATE\nPHASE {_orbitSource.PhaseDegrees:0}°  ALT {_orbitSource.AltitudeMeters / 1000.0:0} KM\n" +
                $"P {attitude.x:0}°  Y {attitude.y:0}°  R {attitude.z:0}°";
        }

        private static float NormalizeSignedDegrees(float value) =>
            Mathf.Repeat(value + 180f, 360f) - 180f;

        private bool IsGroupVisible(SensorGroup group)
        {
            return !_visibleGroups.TryGetValue(group, out bool isVisible) || isVisible;
        }

        private void TogglePause()
        {
            if (_runner != null)
            {
                _runner.IsRunning = !_runner.IsRunning;
                RefreshTelemetry();
            }
        }

        private void ResetSimulation()
        {
            if (_runner != null)
            {
                _runner.ResetSimulation();
                _runner.StepOnce();
            }
        }

        private void CycleSpeed()
        {
            if (_runner == null)
            {
                return;
            }

            _speedIndex = (_speedIndex + 1) % _speedOptions.Length;
            _runner.TimeScale = _speedOptions[_speedIndex];
            _speedButtonText.text = $"TIME {_speedOptions[_speedIndex]:0}×";
        }

        private void CaptureImages()
        {
            if (_episodeExporter == null || !_episodeExporter.RequestCapture())
            {
                return;
            }

            HandleCaptureStatusChanged(_episodeExporter.LastCaptureStatus);
        }

        private void HandleCaptureStatusChanged(string status)
        {
            if (_captureButtonText != null)
            {
                _captureButtonText.text = status;
            }
        }

        private void OnDestroy()
        {
            if (_episodeExporter != null)
            {
                _episodeExporter.CaptureStatusChanged -= HandleCaptureStatusChanged;
            }
        }

        private static void AddHeader(StringBuilder builder, string label)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.Append("<color=#35D5F2><b>").Append(label).AppendLine("</b></color>");
            builder.AppendLine("────────────────────────────────");
        }

        private static void AddRow(StringBuilder builder, string label, string value)
        {
            builder.Append("<color=#8EA9B7>").Append(label).Append(":</color>  ")
                .AppendLine(value);
        }

        private static void AddVector(StringBuilder builder, string label, Vector3d value)
        {
            AddRow(builder, label, $"{value.X:F3}, {value.Y:F3}, {value.Z:F3}");
        }

        private RectTransform CreatePanel(
            string name,
            RectTransform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Color color)
        {
            Image image = CreateUiObject<Image>(name, parent);
            image.color = color;
            SetAnchors(image.rectTransform, anchorMin, anchorMax);
            return image.rectTransform;
        }

        private Text CreateText(
            string name,
            RectTransform parent,
            string value,
            int fontSize,
            FontStyle style,
            TextAnchor alignment,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax)
        {
            Text text = CreateUiObject<Text>(name, parent);
            text.font = _font;
            text.text = value;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = color;
            text.supportRichText = true;
            text.raycastTarget = false;
            SetAnchors(text.rectTransform, anchorMin, anchorMax);
            return text;
        }

        private Button CreateButton(
            string name,
            RectTransform parent,
            string label,
            Vector2 anchorMin,
            Vector2 anchorMax)
        {
            Image image = CreateUiObject<Image>(name, parent);
            image.color = RaisedPanelColor;
            SetAnchors(image.rectTransform, anchorMin, anchorMax);
            Button button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(0.08f, 0.28f, 0.36f, 1f);
            colors.pressedColor = new Color(0.04f, 0.48f, 0.58f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;
            CreateText(
                "Label",
                image.rectTransform,
                label,
                17,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor,
                Vector2.zero,
                Vector2.one);
            return button;
        }

        private InputField CreateInputField(
            string name,
            RectTransform parent,
            string value,
            Vector2 anchorMin,
            Vector2 anchorMax)
        {
            RectTransform root = CreatePanel(name, parent, anchorMin, anchorMax, PanelColor);
            Text text = CreateText(
                "Text",
                root,
                string.Empty,
                17,
                FontStyle.Normal,
                TextAnchor.MiddleLeft,
                TextColor,
                Vector2.zero,
                Vector2.one);
            text.supportRichText = false;
            text.rectTransform.offsetMin = new Vector2(12f, 0f);
            text.rectTransform.offsetMax = new Vector2(-12f, 0f);

            InputField field = root.gameObject.AddComponent<InputField>();
            field.textComponent = text;
            field.targetGraphic = root.GetComponent<Image>();
            field.lineType = InputField.LineType.SingleLine;
            field.text = value;
            return field;
        }

        private Toggle CreateToggle(
            string name,
            RectTransform parent,
            string label,
            Vector2 anchorMin,
            Vector2 anchorMax)
        {
            RectTransform root = CreateRectTransform(name, parent);
            SetAnchors(root, anchorMin, anchorMax);
            Toggle toggle = root.gameObject.AddComponent<Toggle>();

            Image background = CreateUiObject<Image>("Background", root);
            background.color = new Color(0.12f, 0.18f, 0.22f, 1f);
            SetAnchors(background.rectTransform, new Vector2(0f, 0.15f), new Vector2(0.11f, 0.85f));
            Image checkmark = CreateUiObject<Image>("Checkmark", background.rectTransform);
            checkmark.color = AccentColor;
            SetAnchors(checkmark.rectTransform, new Vector2(0.2f, 0.2f), new Vector2(0.8f, 0.8f));

            toggle.targetGraphic = background;
            toggle.graphic = checkmark;
            CreateText(
                "Label",
                root,
                label,
                14,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                TextColor,
                new Vector2(0.15f, 0f),
                Vector2.one);
            return toggle;
        }

        private static T CreateUiObject<T>(string name, RectTransform parent) where T : Graphic
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(T));
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return gameObject.GetComponent<T>();
        }

        private static RectTransform CreateRectTransform(string name, RectTransform parent)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static void SetAnchors(RectTransform rect, Vector2 minimum, Vector2 maximum)
        {
            rect.anchorMin = minimum;
            rect.anchorMax = maximum;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null)
            {
                return;
            }

            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }
    }
}
