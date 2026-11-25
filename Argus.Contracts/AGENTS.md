# AGENTS.md: Argus.Contracts

Versioned Protobuf schemas for every message that crosses a process boundary. Nothing here is
compiled into a committed artifact: each consumer generates its own code. The full rules are in
[README.md](README.md).

| Package | Link | Server → client |
|---|---|---|
| `argus.sim.v1` (`proto/argus/sim/v1/`) | Shared messages; mirrors Core contracts | used by all links |
| `argus.basilisk.v1` | Basilisk-native link (J2000, MRPs, SPICE matrices) | `Argus.Basilisk` → `headless/Host` only |
| `argus.stream.v1` | State stream (CON-1) | `headless/Host` → Unity |
| `argus.gateway.v1` | Controller gateway (CON-3) | `headless/Host` (HOST-6) → HIL adapters, external controllers |
| `argus.render.v1` | Placeholder (D5, G3) | not built yet |

## Check a change

From the repository root, compile every schema (CI does the same with `/usr/include`):

```bash
protoc -I Argus.Contracts/proto -I "$(brew --prefix)/include" --descriptor_set_out=/dev/null $(find Argus.Contracts/proto -name '*.proto')
```

Then rebuild every consumer of the package you changed:

```bash
dotnet build headless/Host/Argus.Simulation.Host.csproj                 # host (Basilisk client, stream server)
dotnet build Argus.Contracts/Unity/Argus.Stream.Contracts.csproj        # Unity plugin (sim + stream only)
cd Argus.Basilisk && .venv/bin/python scripts/generate_protos.py && .venv/bin/python -m pytest -q
```

## Rules

- **v1 is additive only.** Add messages, fields, RPCs and oneof members with new field numbers.
  Never change a field's number, type, name, unit, frame or meaning; a removed field becomes
  `reserved`. A breaking change is a new `v2` package.
- **One PR for both sides.** The Core C# types are the in-process truth and the `.proto` files
  the cross-language truth; a change to one lands with the matching change to the other, and to
  the mappers (`headless/Host/**/*Mapper.cs`, `Assets/ArgusSimulation/Unity/Runtime/StateStreamMapper.cs`).
- **Names carry unit and frame**, in the field name and its comment: suffixes such as `_m`,
  `_m_per_s`, `_rad_per_s`, `_ns`, `_tesla`, and frame tokens `_body`, `_sensor`, `_j2000`,
  `_itrf93`, `_earth_fixed`. The README lists the full sets.
- **buf STANDARD style:** `<Rpc>Request` / `<Rpc>Response`, services end in `Service`, and every
  enum's zero value is `<ENUM>_UNSPECIFIED` and rejected.
- **Presence:** an unset message field or oneof is INVALID_ARGUMENT, never defaulted. A field added
  to a frozen message is the exception: older clients cannot send it, so its default must be the
  safe behaviour. If zero is a plausible wrong value for a scalar, declare it `optional`.
- **D8:** Unity never sees `argus.basilisk.v1`. The Unity plugin project generates only the sim
  and stream messages and the stream client.
- **C# aliases:** mapping code uses `using PbSim = Argus.Contracts.Sim.V1;` and
  `using PbBasilisk = Argus.Contracts.Basilisk.V1;`; never import those namespaces unqualified,
  because their type names collide with Core.
- Never commit generated code (`Argus.Basilisk/generated/`, `obj/`,
  `Assets/ArgusSimulation/Plugins/`).
- Update the README's message descriptions and the endpoint table when a link changes.
