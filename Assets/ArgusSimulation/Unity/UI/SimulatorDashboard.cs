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
            HostSensors,
            Cameras,
            SensorAvailability
        }

        private static readonly Color PanelColor = new Color(0.025f, 0.043f, 0.067f, 0.96f);
        private static readonly Color RaisedPanelColor = new Color(0.045f, 0.075f, 0.105f, 0.98f);
        private static readonly Color AccentColor = new Color(0.12f, 0.82f, 0.95f, 1f);
        private static readonly Color TextColor = new Color(0.86f, 0.92f, 0.96f, 1f);
        private static readonly Color MutedTextColor = new Color(0.55f, 0.66f, 0.72f, 1f);

        private static readonly Color GoodColor = new Color(0.3f, 1f, 0.58f);
        private static readonly Color WaitColor = new Color(0.98f, 0.72f, 0.22f);
        private static readonly Color BadColor = new Color(1f, 0.45f, 0.4f);

        private readonly Dictionary<SensorGroup, bool> _visibleGroups =
            new Dictionary<SensorGroup, bool>();

        // The host owns the run (G2): Unity only follows its stream, so there is no pause, step,
        // time scale or reset here, and orbit and attitude nudges wait for engine commands (UNITY-11).
        private SimulationRunner _runner;
        private StateStreamClient _streamClient;
        private CubeSatCameraRig _cameraRig;
        private NadirTilePriority _nadirPriority;
        private NavigationEpisodeExporter _episodeExporter;
        private Camera _mainCamera;
        private Font _font;
        private Text _telemetryText;
        private Text _statusText;
        private Text _captureButtonText;
        private Text _streamText;
        private GameObject _settingsPanel;
        private InputField _gtDateField;
        private InputField _gtLayerField;
        private Text _settingsStatusText;
        private float _nextTelemetryRefresh;

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

            _streamClient = _runner.StateSource as StateStreamClient;

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

                _nadirPriority = spacecraft.GetComponent<NadirTilePriority>();
                if (_nadirPriority == null)
                {
                    _nadirPriority = spacecraft.AddComponent<NadirTilePriority>();
                }
                _nadirPriority.Configure(_runner);
            }

            OrbitTrailRenderer trail = FindAnyObjectByType<OrbitTrailRenderer>();
            if (trail == null)
            {
                GameObject trailObject = new GameObject("Orbit Visualization");
                trail = trailObject.AddComponent<OrbitTrailRenderer>();
            }
            trail.Configure(_runner);
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
            BuildStreamPanel(canvasTransform);
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

            CreateText(
                "Source Badge",
                topBar,
                "HOST STREAM",
                17,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                MutedTextColor,
                new Vector2(0.67f, 0f),
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

        // Where the stream comes from and how it is doing; the host's run is the only simulation.
        private void BuildStreamPanel(RectTransform parent)
        {
            RectTransform panel = CreatePanel(
                "Stream Status",
                parent,
                new Vector2(0.012f, 0.805f),
                new Vector2(0.688f, 0.888f),
                new Color(0.02f, 0.04f, 0.06f, 0.90f));

            CreateText(
                "Stream Label",
                panel,
                "HOST STREAM",
                14,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                AccentColor,
                new Vector2(0.012f, 0.05f),
                new Vector2(0.18f, 0.95f));

            _streamText = CreateText(
                "Stream Text",
                panel,
                "",
                14,
                FontStyle.Normal,
                TextAnchor.MiddleLeft,
                TextColor,
                new Vector2(0.19f, 0.05f),
                new Vector2(0.988f, 0.95f));
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
                "ORBIT TRUTH", "HOST SENSORS", "CAMERAS", "AVAILABILITY"
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
            RefreshStream();
            if (_runner == null || !_runner.HasState)
            {
                _telemetryText.text = "Waiting for the host's first state…";
                return;
            }

            SpacecraftState state = _runner.LastState.Spacecraft;
            StringBuilder builder = new StringBuilder(1400);

            if (IsGroupVisible(SensorGroup.OrbitTruth))
            {
                AddHeader(builder, "SPACECRAFT TRUTH");
                AddRow(builder, "UTC", state.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                AddRow(builder, "Simulation time", $"{state.SimulationTimeSeconds,10:F1} s");
                AddRow(builder, "Sequence", state.Sequence.ToString());
                AddVector(builder, "ECEF position km", state.PositionEcefMeters / 1000.0);
                AddVector(builder, "ECEF velocity m/s", state.VelocityEcefMetersPerSecond);
                AddRow(builder, "Speed", $"{state.VelocityEcefMetersPerSecond.Magnitude / 1000.0:F3} km/s");
                Quaterniond q = state.BodyToEcef;
                AddRow(builder, "Body→ECEF q", $"{q.X:F4}, {q.Y:F4}, {q.Z:F4}, {q.W:F4}");
            }

            if (IsGroupVisible(SensorGroup.HostSensors))
            {
                // TODO(G2): list the host's sensor frames from StateStreamClient.OutputProduced
                // (UNITY-7, #61); Unity no longer runs sensors of its own.
                AddHeader(builder, "HOST SENSORS");
                AddRow(builder, "Status", "Received on the stream; shown here by UNITY-7");
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
                // TODO(G2): list sensors from the received SensorOutputSet instead of this fixed
                // text. The accelerometer (IMU) and magnetometer now exist as Basilisk-sourced
                // models (Core/Sensors) but publish nothing until the Basilisk service runs.
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

        // The top bar's status and the stream panel: whether Unity is following the host.
        private void RefreshStream()
        {
            if (_streamClient == null)
            {
                SetStatus("● NO HOST STREAM", BadColor);
                _streamText.text = "This scene has no StateStreamClient to follow the host.";
                return;
            }

            string generation = _streamClient.RunGeneration;
            _streamText.text =
                $"{_streamClient.Endpoint}   {_streamClient.Status.ToString().ToUpperInvariant()}" +
                (generation == null ? "" : $"   run {generation.Substring(0, Math.Min(8, generation.Length))}") +
                (_runner != null && _runner.HasState ? $"   ×{_runner.SimulationRate:0.#} real time" : "") +
                (_streamClient.StatusMessage.Length > 0 ? "\n" + _streamClient.StatusMessage : "");

            switch (_streamClient.Status)
            {
                case StateStreamStatus.Streaming when _nadirPriority != null && _nadirPriority.IsWarmingUp:
                    SetStatus($"● LOADING NADIR {_nadirPriority.WarmUpProgress:0}%", WaitColor);
                    break;
                case StateStreamStatus.Streaming:
                    SetStatus("● FOLLOWING HOST", GoodColor);
                    break;
                case StateStreamStatus.Disconnected:
                    SetStatus("● HOST DISCONNECTED", BadColor);
                    break;
                default:
                    SetStatus("● CONNECTING TO HOST", WaitColor);
                    break;
            }
        }

        private void SetStatus(string text, Color color)
        {
            _statusText.text = text;
            _statusText.color = color;
        }

        private bool IsGroupVisible(SensorGroup group)
        {
            return !_visibleGroups.TryGetValue(group, out bool isVisible) || isVisible;
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
