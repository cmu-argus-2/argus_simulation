# Argus Simulator Code Organization

This is the authoritative map for class placement. Class namespaces remain
`Argus.Simulation.Core` and `Argus.Simulation.Unity`; folders describe ownership and
dependencies rather than creating deeply nested namespaces.

## Source tree

```text
Assets/ArgusSimulation/
├── Core/                         # Pure C#; no Unity or Cesium dependencies
│   ├── Abstractions/             # Replaceable backend/service interfaces
│   ├── Contracts/                # State, command, step, and configuration DTOs
│   ├── Dynamics/                 # Analytic dynamics and future engine implementations
│   ├── Imaging/                  # Camera/render contracts and imagery helpers
│   ├── Math/                     # Double-precision vectors and quaternions
│   ├── Runtime/                  # Headless orchestration and gateway
│   └── Sensors/                  # Standard sensor envelopes and models
│
├── Unity/                        # Unity-dependent adapters and presentation
│   ├── Cameras/                  # Unity camera rigs and render implementation
│   ├── Cesium/                   # Cesium credentials, imagery, and globe controls
│   ├── Export/                   # Image, metadata, and reference-map exporters
│   ├── Runtime/                  # MonoBehaviour adapters to the headless core
│   ├── Sensors/                  # Temporary Unity sensor implementations
│   ├── UI/                       # Mission-control dashboard
│   └── Visualization/            # CubeSat model, pose, and orbit rendering
│
├── Editor/
│   └── Scene/                    # Unity Editor scene/bootstrap tools
│
├── Scenes/                       # Serialized Unity scenes
└── Tests/
    ├── EditMode/Core/            # Pure/core contract tests
    └── PlayMode/UI/              # Unity integration and dashboard tests
```

## Class map

| Responsibility | Main classes |
|---|---|
| Engine interfaces | `ISimulationEngine`, `ISpacecraftStateSource` |
| Rendering interface | `IImageRenderer` |
| Simulation contracts | `SimulationConfiguration`, `SimulationStepInput`, `SimulationSnapshot`, `ActuatorCommandSet`, `SpacecraftState` |
| Development dynamics | `AnalyticSimulationEngine`, `CircularOrbitModel` |
| Headless orchestration | `SimulationGateway` |
| Sensor contracts | `SensorFrame<TPayload>` |
| Camera/image contracts | `CameraIntrinsics`, `RenderRequest`, `ImageFrame` |
| Unity runtime bridge | `SimulationRunner`, `AnalyticOrbitStateSource` |
| Unity camera system | `CubeSatCameraRig` |
| Cesium integration | `CesiumIonEnvironmentLoader`, `NasaGibsRasterController`, `RuntimeGlobeCameraController` |
| Export | `NavigationEpisodeExporter`, `CesiumReferenceMapExporter` |
| Visualization | `CesiumSpacecraftPoseDriver`, `CubeSatVisualModel`, `OrbitTrailRenderer` |
| Temporary sensors | `MockSensorSuite` |
| GUI | `SimulatorDashboard` |
| Scene creation | `FoundationSceneBuilder` |

## Placement rules

- Put a class in `Core` only when it compiles without Unity, Cesium, or editor APIs.
- Put cross-process messages and stable DTOs in `Core/Contracts`.
- Put an interface in `Core/Abstractions` when multiple implementations are expected.
- Put physical sensor behavior in `Core/Sensors`; Unity may supply only a rendering or
  geometry adapter.
- Put camera calibration and frame formats in `Core/Imaging`; put Unity rasterization in
  `Unity/Cameras`.
- Put network transports in future top-level packages, not in Unity UI classes.
- Keep one public top-level class per file, except a small enum that exists only to
  describe the adjacent contract.
- Tests mirror the responsibility of the production code they verify.

## Planned additions

```text
Core/Sensors/Camera/
├── CameraModelBase.cs
├── ArducamImx708CameraModel.cs
└── NadirGroundTruthCameraModel.cs

External services/packages:
├── Argus.Contracts/              # Protobuf schemas
├── Argus.Basilisk/               # Python Basilisk backend
├── Argus.Agent/                  # Training environment
├── Argus.Hardware/               # Flight-computer protocol adapters
└── Argus.Export/                 # Headless dataset/replay writers
```

Unity asset references are preserved during moves by keeping each `.cs.meta` file with
its source file. Do not delete or regenerate those metadata files during refactoring.
