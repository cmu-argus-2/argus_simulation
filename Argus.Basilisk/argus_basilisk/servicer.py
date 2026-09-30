"""BasiliskSimulationService implementation. Replace each body; README.md defines the semantics."""

import grpc

from .contracts import service_grpc


class BasiliskSimulationServicer(service_grpc.BasiliskSimulationServiceServicer):
    """Handoff skeleton: every RPC aborts with UNIMPLEMENTED."""

    def ConfigureRun(self, request, context):
        # TODO(basilisk-team): README §3 "ConfigureRun": validate (validation.py), verify kernels
        # (kernels.py), build the simulation (scenario.py, sensors.py), InitializeSimulation, and
        # echo every ConfigureRunResponse field.
        context.abort(grpc.StatusCode.UNIMPLEMENTED, "ConfigureRun: Basilisk scenario not implemented")

    def Reset(self, request, context):
        # TODO(basilisk-team): README §3 "Reset": rebuild a new SimBaseClass and modules from the
        # stored request (never reuse them) and echo run_id.
        context.abort(grpc.StatusCode.UNIMPLEMENTED, "Reset: not implemented")

    def Step(self, request, context):
        # TODO(basilisk-team): README §3 "Step(n)": check run_id, sequence and time, run to t_n,
        # then write command n, check message freshness, and return the StepResponse (§7).
        context.abort(grpc.StatusCode.UNIMPLEMENTED, "Step: not implemented")
