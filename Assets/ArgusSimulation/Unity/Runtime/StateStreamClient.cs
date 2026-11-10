namespace Argus.Simulation.Unity
{
    // PLACEHOLDER: not implemented, not referenced.
    // TODO(G2): receive the decimated SimulationState stream from headless/Host and expose it as an
    // ISimulationStateSource, so SimulationRunner becomes a follower and Unity stops running the
    // engine and sensors itself. Also expose the host's SensorOutputSets (for example an
    // OutputProduced event) so the dashboard reads the latest-per-sensor projection.
    // CON-1 selects native gRPC with Grpc.Net.Client + YetAnotherHttpHandler (Http2Only for h2c).
    // Import generated Argus.Stream.Contracts per Argus.Contracts/README.md. Clear interpolation
    // history when run_generation changes; cached frames retain their acquisition metadata.
    internal sealed class StateStreamClient
    {
    }
}
