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
│   ├── Basilisk/                 # Internal Basilisk-to-Argus mapping (D8; visible to host and tests)
│   ├── Contracts/                # State, command, step, and configuration DTOs
│   ├── Dynamics/                 # Analytic dynamics (development and test fixture)
│   ├── Imaging/                  # Camera/render contracts and imagery helpers
│   ├── Math/                     # Double-precision vectors, quaternions, MRPs, 3x3 matrices
│   ├── Recording/                # Run recorder, the single export route (placeholder)
│   ├── Runtime/                  # Headless orchestration and gateway
│   └── Sensors/                  # Standard sensor envelopes and models
│       └── Camera/               # Camera sensor models (placeholders)
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
└── Host/                         # Headless host: BasiliskEngine gRPC client, P0 scenario
Argus.Contracts/proto/argus/      # Protobuf v1: sim/v1 (shared), basilisk/v1; stream, gateway, render (placeholders)
Argus.Basilisk/                   # Basilisk service skeleton + brief (argus_basilisk/, kernel_sets/, scripts/)
Argus.Agent/                      # Training environment and agent SDK (README placeholder)
Argus.Hardware/                   # Flight-computer adapters (README placeholder)
```

## Class map

| Responsibility | Main classes |
|---|---|
| Engine interfaces | `ISimulationEngine`, `ISpacecraftStateSource` |
| Rendering interface | `IImageRenderer` |
| Simulation contracts | `SimulationConfiguration`, `SimulationStepInput`, `SimulationState`, `ActuatorCommandSet`, `SpacecraftState`, `ReferenceFrame`, `EnvironmentState`, `ClassicalOrbitElements`, `SpacecraftConfiguration` |
| Development dynamics | `AnalyticSimulationEngine`, `CircularOrbitModel` |
| Headless orchestration | `SimulationGateway` |
| Headless host (`headless/Host`) | `BasiliskEngine`, `BasiliskProtoMapper`, `HostOptions`, `HostScenario`, `Program` |
| Basilisk mapping (internal) | `BasiliskTime`, `BasiliskSpacecraftState`, `BasiliskPlanetState`, `BasiliskSensorSample`, `BasiliskStepState`, `BasiliskStateMapper` |
| Sensor contracts and runtime | `ISensor`, `SensorModel<TPayload>`, `SensorManager`, `SensorFrame<TPayload>` |
| Implemented sensor models | `IdealBodyRateSensorModel`; Basilisk-sourced `BackendSensorModel<TMeasurement>`, `ImuSensor`, `MagnetometerSensor`, `LightSensor`, `SensorFactory`, `SensorMeasurementSet` |
| P0 sensor contracts | `ImuMeasurement`, `MagnetometerMeasurement`, `LightSensorMeasurement`, `GyroscopeProfile`, `AccelerometerProfile`, `ImuProfile`, `MagnetometerProfile`, `LightSensorProfile`, `SensorConfiguration`, `SensorKind` |
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
- Put gRPC and Protobuf code only in `headless/Host`, with generated code `Access=Internal`.
  Core never references Google.Protobuf or Grpc, and Basilisk-native Core types are
  internal. Future transports go in top-level packages, never in Unity UI classes.
- Keep one public top-level class per file, except a small enum that exists only to
  describe the adjacent contract.
- Tests mirror the responsibility of the production code they verify.

## Placeholders and TODOs

Planned work is marked in the code with `TODO(<ids>)`, where each id is a decision or gap in
[target-architecture.md](target-architecture.md) (for example `TODO(G2)` or `TODO(D7, G1)`) or
an area: `sensors`, `hardware`, `fixture` (the analytic engine), `cesium`, `basilisk-team` or
`spice-team`. List them all with `git grep -n "TODO("`.

Where the planned file does not exist yet, a placeholder holds its place. C# placeholders are
empty `internal` types that nothing references, so they add no API and no behaviour; Python
placeholders are docstring-only modules; proto placeholders declare only their package. Each
says what to build, and its tag says why.

```text
Core/Sensors/
├── GnssSensor.cs, SunSensor.cs, StarTrackerSensor.cs     # Upcoming sensors (TODO(sensors))
├── PowerTelemetrySensor.cs, ThermalSensor.cs
├── RadioLinkSensor.cs, RadiationSensor.cs
└── Camera/CameraModel.cs                                # TODO(D5, G3)
    Camera/ArducamImx708CameraModel.cs                   # TODO(D5)
    Camera/NadirGroundTruthCameraModel.cs                # TODO(D5, D7)
Core/Recording/RunRecorder.cs                            # TODO(D6)
Core/Abstractions/ISimulationStateSource.cs              # TODO(G2)
Unity/Cameras/UnityImageRenderer.cs                      # TODO(D5)
Unity/Runtime/StateStreamClient.cs                       # TODO(G2)
Unity/Visualization/SunLightDriver.cs                    # TODO(D4)
headless/Host/Streaming/StateStreamServer.cs             # TODO(G2)
headless/Host/Gateway/GatewayServer.cs                   # TODO(D7, G1)
Argus.Contracts/proto/argus/{stream,gateway,render}/v1/  # TODO(G2), TODO(D7, G1), TODO(D5, G3)
Argus.Basilisk/argus_basilisk/{validation,scenario,sensors,kernels,seeds}.py  # TODO(basilisk-team)
Argus.Basilisk/scripts/fetch_kernels.py                  # TODO(spice-team)
Argus.Agent/README.md, Argus.Hardware/README.md          # TODO(D7, G1)
```

When a placeholder is implemented, make it public if it is API, register it where it is used,
and remove its TODO.

Unity asset references are preserved during moves by keeping each `.cs.meta` file with
its source file. Do not delete or regenerate those metadata files during refactoring.
