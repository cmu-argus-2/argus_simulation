"""BasiliskSimulationService implementation (README §3 lifecycle, §11 error codes)."""

from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
import threading

import grpc

from .contracts import service_grpc, service_pb
from .kernels import KernelSet, KernelSetError, load_kernel_set
from .scenario import BASILISK_VERSION, KernelLoadError, Scenario, StaleMessageError
from .validation import (
    SUPPORTED_SENSOR_KINDS,
    WMM2025_VALIDITY,
    UtcWindow,
    ValidationError,
    check_utc_in_range,
    configure_run_problems,
    timestamp_to_ns,
    validate_configure_run,
    validate_step_command,
)

EMPTY = "EMPTY"
READY = "READY"
RUNNING = "RUNNING"
FAILED = "FAILED"

# Lets validation report INVALID_ARGUMENT and UNIMPLEMENTED when the kernel set cannot be read.
_UNBOUNDED_UTC = UtcWindow(
    start=datetime(1, 1, 1, tzinfo=timezone.utc),
    end=datetime(9999, 12, 31, 23, 59, 59, tzinfo=timezone.utc),
)


class _Abort(Exception):
    """A gRPC status to return; raised inside the servicer, mapped once in _call."""

    def __init__(self, code: str, details: str):
        self.code = code
        self.details = details
        super().__init__(details)


@dataclass
class _Run:
    request: service_pb.ConfigureRunRequest
    kernel_set: KernelSet
    scenario: Scenario
    state: str = READY
    next_sequence: int = 0

    @property
    def run_id(self) -> str:
        return self.request.configuration.run_id


class BasiliskSimulationServicer(service_grpc.BasiliskSimulationServiceServicer):
    """One run per process; calls are strictly sequential (README §2)."""

    def __init__(self, *, data_dir: str | Path | None = None):
        """Args:
            data_dir: Directory holding kernel_sets/ and kernels/; defaults to Argus.Basilisk/.
        """
        self._data_dir = data_dir
        self._lock = threading.Lock()
        self._run: _Run | None = None

    @property
    def state(self) -> str:
        return EMPTY if self._run is None else self._run.state

    def ConfigureRun(self, request, context):
        return self._call(context, self._configure_run, request)

    def Reset(self, request, context):
        return self._call(context, self._reset, request)

    def Step(self, request, context):
        return self._call(context, self._step, request)

    def _call(self, context, handler, request):
        # The server already allows one RPC at a time; this keeps a concurrent call from ever
        # reaching Basilisk if that setting changes.
        if not self._lock.acquire(blocking=False):
            context.abort(grpc.StatusCode.RESOURCE_EXHAUSTED, "another call is in progress; calls are sequential")
        try:
            return handler(request)
        except _Abort as error:
            failure = error
        finally:
            self._lock.release()
        context.abort(grpc.StatusCode[failure.code], failure.details)

    # --- ConfigureRun --------------------------------------------------------------------

    def _configure_run(self, request) -> service_pb.ConfigureRunResponse:
        # 1. Discard any existing run.
        self._run = None

        # 2. Validate every field, against the kernel set's coverage, before touching Basilisk.
        kernel_set = self._load_kernel_set_for(request)
        try:
            validate_configure_run(
                request,
                coverage_utc=kernel_set.coverage_utc,
                supported_sensor_kinds=SUPPORTED_SENSOR_KINDS,
            )
        except ValidationError as error:
            raise _Abort(error.code, error.details) from None

        # 3. Build and initialize; run no step.
        scenario = self._build(request.configuration, kernel_set)

        # 4. Store the request, end READY, and echo every field.
        self._run = _Run(request=request, kernel_set=kernel_set, scenario=scenario)
        configuration = request.configuration
        return service_pb.ConfigureRunResponse(
            run_id=configuration.run_id,
            kernel_set_id=kernel_set.kernel_set_id,
            fixed_step_ns=configuration.fixed_step_ns,
            configured_sensor_ids=[sensor.definition.sensor_id for sensor in configuration.sensors],
            basilisk_version=BASILISK_VERSION,
            spice_earth_frame=scenario.spice_earth_frame,
        )

    def _load_kernel_set_for(self, request) -> KernelSet:
        """Verify the kernel set; field problems outrank a missing or altered kernel set."""
        try:
            return load_kernel_set(request.configuration.kernel_set_id, data_dir=self._data_dir)
        except KernelSetError as kernel_error:
            problems = [
                problem
                for problem in configure_run_problems(
                    request, coverage_utc=_UNBOUNDED_UTC, supported_sensor_kinds=SUPPORTED_SENSOR_KINDS)
                if problem.code != "OUT_OF_RANGE"
            ]
            if problems:
                code = problems[0].code
                raise _Abort(code, "; ".join(p.details for p in problems if p.code == code)) from None
            raise _Abort(kernel_error.code, kernel_error.details) from None

    def _build(self, configuration, kernel_set: KernelSet) -> Scenario:
        try:
            return Scenario(configuration, kernel_set)
        except KernelLoadError as error:
            raise _Abort("FAILED_PRECONDITION", str(error)) from None
        except Exception as error:  # A Basilisk failure (README §11).
            raise _Abort("INTERNAL", f"building the simulation failed: {error!r}") from None

    # --- Reset ---------------------------------------------------------------------------

    def _reset(self, request) -> service_pb.ResetResponse:
        run = self._require_run(request.run_id)
        # Never reuse modules (README §3 Reset): verify the files again and build anew.
        run.state = FAILED
        try:
            kernel_set = load_kernel_set(run.kernel_set.kernel_set_id, data_dir=self._data_dir)
        except KernelSetError as error:
            raise _Abort(error.code, error.details) from None
        run.scenario = None
        run.scenario = self._build(run.request.configuration, kernel_set)
        run.kernel_set = kernel_set
        run.state = READY
        run.next_sequence = 0
        return service_pb.ResetResponse(run_id=run.run_id)

    # --- Step ----------------------------------------------------------------------------

    def _step(self, request) -> service_pb.StepResponse:
        # 1. Run ID and sequence.
        run = self._require_run(request.run_id)
        if run.state == FAILED:
            raise _Abort("FAILED_PRECONDITION", f"run {run.run_id!r} FAILED; call Reset")
        sequence = request.sequence
        if sequence != run.next_sequence:
            raise _Abort("FAILED_PRECONDITION", f"sequence: expected {run.next_sequence}, got {sequence}")

        # 2. Time.
        configuration = run.request.configuration
        t_n = sequence * configuration.fixed_step_ns
        if request.sim_time_ns != t_n:
            raise _Abort(
                "INVALID_ARGUMENT",
                f"sim_time_ns: expected {t_n} (sequence x fixed_step_ns), got {request.sim_time_ns}")

        # 3. Command.
        if not request.HasField("command"):
            raise _Abort("INVALID_ARGUMENT", "command: required message is unset")
        try:
            validate_step_command(request.command)
            # 4. epoch + t_n inside coverage, before executing: CSPICE aborts the process outside it.
            has_magnetometer = any(
                sensor.WhichOneof("profile") == "magnetometer" for sensor in configuration.sensors)
            check_utc_in_range(
                timestamp_to_ns(configuration.epoch_utc) + t_n,
                "configuration.epoch_utc + sim_time_ns",
                coverage_utc=run.kernel_set.coverage_utc,
                wmm_validity=WMM2025_VALIDITY if has_magnetometer else None,
            )
        except ValidationError as error:
            raise _Abort(error.code, error.details) from None

        # 5 to 7. Run to t_n and read every returned message; a failure ends the run.
        try:
            response = run.scenario.step(sequence)
        except StaleMessageError as error:
            run.state = FAILED
            raise _Abort("INTERNAL", str(error)) from None
        except Exception as error:  # A Basilisk failure (README §11).
            run.state = FAILED
            raise _Abort("INTERNAL", f"Step({sequence}) failed: {error!r}") from None

        # 8. Echo and advance.
        response.run_id = run.run_id
        response.sequence = sequence
        run.state = RUNNING
        run.next_sequence = sequence + 1
        return response

    def _require_run(self, run_id: str) -> _Run:
        if self._run is None:
            raise _Abort("FAILED_PRECONDITION", "no run is configured; call ConfigureRun")
        if run_id != self._run.run_id:
            raise _Abort("FAILED_PRECONDITION", f"run_id: expected {self._run.run_id!r}, got {run_id!r}")
        return self._run
