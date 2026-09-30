namespace Argus.Simulation.Host
{
    // PLACEHOLDER: not implemented, not referenced.
    // TODO(D7, G1): the controller link for HIL flight computers (Argus.Hardware) and other
    // external controllers over argus/gateway/v1. Observations out (controller-visible sensor frames
    // only, never truth), commands in; command validation against actuator limits, authority,
    // heartbeat and failsafe; every command also goes to the recorder's command log (D6). Add a
    // GrpcServices="Server" item for this schema in the host csproj.
    internal sealed class GatewayServer
    {
    }
}
