using System.Runtime.CompilerServices;

// Core/Basilisk types are internal so only the headless host and the EditMode tests (same assembly
// name in Unity and headless/) can see them (D8).
[assembly: InternalsVisibleTo("Argus.Simulation.Host")]
[assembly: InternalsVisibleTo("Argus.Simulation.Tests.EditMode")]
