namespace Argus.Simulation.Unity
{
    // PLACEHOLDER: not implemented, not referenced.
    // TODO(G2): receive the decimated SimulationState stream from headless/Host and expose it as an
    // ISimulationStateSource, so SimulationRunner becomes a follower and Unity stops running the
    // engine and sensors itself. Unity's HttpClient does not speak HTTP/2, so choose a transport
    // Unity supports (for example gRPC-Web or Cysharp YetAnotherHttpHandler).
    internal sealed class StateStreamClient
    {
    }
}
