# Argus Simulator Code Organization

This is the authoritative map for class placement. Class namespaces remain
`Argus.Simulation.Core` and `Argus.Simulation.Unity`; folders describe ownership and
dependencies rather than creating deeply nested namespaces. The target design these folders
work toward is in [target-architecture.md](target-architecture.md).

## Source tree

```text
Assets/ArgusSimulation/
├── Core/                         # Pure C#; no Unity or Cesium dependencies (noEngineReferences)
│   ├── Abstractions/             # Replaceable backend/service interfaces
│   ├── Basilisk/                 # Planned: BasiliskEngine adapter (README only)
│   ├── Contracts/                # State, command, step, and configuration DTOs
│   ├── Dynamics/                 # Analytic dynamics (development and test fixture)
│   ├── Imaging/                  # Camera/render contracts and imagery helpers
│   ├── Math/                     # Double-precision vectors, quaternions, MRPs, 3x3 matrices
│   ├── Recording/                # Planned: run recorder, the single export route (README only)
│   ├── Runtime/                  # Headless orchestration and gateway
│   └── Sensors/                  # Standard sensor envelopes and models
│       └── Camera/               # Planned: camera sensor models (README only)
│
├── Unity/                        # Unity-dependent adapters and presentation
│   ├── Cameras/                  # Unity camera rigs and render implementation
│   ├── Cesium/                   # Cesium credentials, imagery, and globe controls
│   ├── Export/                   # Image, metadata, and reference-map exporters
│   ├── Runtime/                  # MonoBehaviour adapters to the headless core
│   ├── Sensors/                  # Temporary bridge to the Core SensorManager (no sensor behaviour)
│   ├── UI/                       # Mission-control dashboard
│   └── Visualization/            # CubeSat model, pose, and orbit rendering
│
├── Editor/
│   └── Scene/                    # Unity Editor scene/bootstrap tools
│
├── Scenes/                       # Serialized Unity scenes
└── Tests/
    ├── EditMode/Core/            # Pure/core contract tests (also run by headless/)
    ├── PlayMode/Sensors/         # Unity sensor-runtime integration tests
    └── PlayMode/UI/              # Unity integration and dashboard tests

headless/                         # dotnet build of Core and the EditMode tests (no Unity)
Argus.Contracts/                  # Planned: Protobuf schemas (README only)
Argus.Basilisk/                   # Planned: Basilisk service with SPICE (README only)
```

## Class map

| Responsibility | Main classes |
|---|---|
| Engine interfaces | `ISimulationEngine`, `ISpacecraftStateSource` |
| Rendering interface | `IImageRenderer` |
| Simulation contracts | `SimulationConfiguration`, `SimulationStepInput`, `SimulationSnapshot`, `ActuatorCommandSet`, `SpacecraftState`, `ReferenceFrame`, `EnvironmentState` |
| Development dynamics | `AnalyticSimulationEngine`, `CircularOrbitModel` |
| Headless orchestration | `SimulationGateway` |
| Sensor contracts and runtime | `ISensor`, `SensorModel<TPayload>`, `SensorManager`, `SensorFrame<TPayload>` |
| Implemented sensor model | `IdealBodyRateSensorModel` |
| Camera/image contracts | `CameraIntrinsics`, `RenderRequest`, `ImageFrame` |
| Unity runtime bridge | `SimulationRunner`, `AnalyticOrbitStateSource` |
| Unity camera system | `CubeSatCameraRig` |
| Cesium integration | `CesiumIonEnvironmentLoader`, `NasaGibsRasterController`, `RuntimeGlobeCameraController` |
| Export | `NavigationEpisodeExporter`, `CesiumReferenceMapExporter` |
| Visualization | `CesiumSpacecraftPoseDriver`, `CubeSatVisualModel`, `OrbitTrailRenderer` |
| Unity sensor bridge | `SimulationSensorRuntime` |
| GUI | `SimulatorDashboard` |
| Scene creation | `FoundationSceneBuilder` |

## Placement rules

- Put a class in `Core` only when it compiles without Unity, Cesium, or editor APIs. The
  Core asmdef sets `noEngineReferences`, and `headless/` must still build.
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

Folders marked planned above exist today with only a README that states their owner and
status. Planned code, including code for those folders:

```text
Core/Sensors/Camera/
├── CameraModel.cs
├── ArducamImx708CameraModel.cs
└── NadirGroundTruthCameraModel.cs

Core/Recording/
└── RunRecorder.cs                # Snapshots, every SensorFrame, gateway command log

Core/Basilisk/
└── BasiliskEngine.cs             # ISimulationEngine adapter; gRPC client location open (target §11)

Unity/Cameras/
└── UnityImageRenderer.cs         # IImageRenderer implementation

External services/packages:
├── Argus.Contracts/              # Protobuf schemas
├── Argus.Basilisk/               # Python Basilisk service, including SPICE
├── Argus.Agent/                  # Training environment
└── Argus.Hardware/               # Flight-computer protocol adapters
```

Unity asset references are preserved during moves by keeping each `.cs.meta` file with
its source file. Do not delete or regenerate those metadata files during refactoring.
