namespace Argus.Simulation.Host
{
    // PLACEHOLDER: not implemented, not referenced.
    // TODO(G2): serve a decimated SimulationState stream to Unity over argus/stream/v1 (spacecraft, environment,
    // sensor frames). It must never block the step loop: drop or decimate for slow clients. The
    // host csproj generates every proto as a client today; add a GrpcServices="Server" item for
    // this schema.
    internal sealed class StateStreamServer
    {
    }
}
