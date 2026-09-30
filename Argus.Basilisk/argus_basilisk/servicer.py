"""BasiliskSimulationService implementation. Replace each body; README.md defines the semantics."""

import grpc

from .contracts import service_grpc


class BasiliskSimulationServicer(service_grpc.BasiliskSimulationServiceServicer):
    """Handoff skeleton: every RPC aborts with UNIMPLEMENTED."""

    def ConfigureRun(self, request, context):  # README "ConfigureRun"
        context.abort(grpc.StatusCode.UNIMPLEMENTED, "ConfigureRun: Basilisk scenario not implemented")

    def Reset(self, request, context):  # README "Reset"
        context.abort(grpc.StatusCode.UNIMPLEMENTED, "Reset: not implemented")

    def Step(self, request, context):  # README "Step"
        context.abort(grpc.StatusCode.UNIMPLEMENTED, "Step: not implemented")
