"""BSK-3 service acceptance (README §13) over a real in-process gRPC server.

Needs bsk==2.11.1 (the basilisk extra). The live-run tests also need the P0 kernel set
argus-2026-10-20 in kernels/, and the SPICE reference test needs argus-2026-09-25
(scripts/fetch_kernels.py); each is skipped without its files.
"""

from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import os
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace

import grpc
import numpy as np
import pytest

pytest.importorskip("Basilisk")

from Basilisk.architecture import messaging  # noqa: E402
from Basilisk.utilities import RigidBodyKinematics as rbk  # noqa: E402

from argus_basilisk import servicer as servicer_module  # noqa: E402
from argus_basilisk.contracts import service_grpc, service_pb  # noqa: E402
from argus_basilisk.kernels import KernelCoverage, KernelSet, KernelSetError, load_kernel_set  # noqa: E402
from test_validation import p0_default_request, zero_command  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
STEP_NS = 100_000_000

P0_KERNEL_SET = "argus-2026-10-20"
# README §13: the reference values below come from this set's Earth PCK.
REFERENCE_KERNEL_SET = "argus-2026-09-25"


def kernels_present(kernel_set_id):
    try:
        load_kernel_set(kernel_set_id)
        return True
    except KernelSetError:
        return False


real_kernels = pytest.mark.skipif(
    not kernels_present(P0_KERNEL_SET), reason=f"run scripts/fetch_kernels.py {P0_KERNEL_SET} first")
reference_kernels = pytest.mark.skipif(
    not kernels_present(REFERENCE_KERNEL_SET),
    reason=f"needs the {REFERENCE_KERNEL_SET} files in kernels/; NAIF has deleted its Earth PCK")

# Step(0) at 2025-01-15T00:00Z: the inputs of Core BasiliskStateMapperTests (README §13).
J2000_TO_ITRF93 = [
    [-0.41275331697402873, 0.9108423308589297, 0.0009734692644210158],
    [-0.9108395986434633, -0.4127544635018774, 0.0022312333567098],
    [0.0024341055752874208, 3.42746148280378e-05, 0.9999970369732598],
]
J2000_TO_ITRF93_RATE = [
    [-6.641947228340686e-05, -3.0098530839345522e-05, 1.62807911771243e-07],
    [3.0098447352340842e-05, -6.641967177075282e-05, -7.093902117028215e-08],
    [8.609191046469471e-11, -7.49538065266115e-11, -2.0698839971282307e-13],
]
SUN_FROM_EARTH_J2000_M = [61571146677.18358, -122621156753.13065, -53154573837.481125]


@pytest.fixture
def service():
    servicer = servicer_module.BasiliskSimulationServicer()
    server = grpc.server(ThreadPoolExecutor(max_workers=2))
    service_grpc.add_BasiliskSimulationServiceServicer_to_server(servicer, server)
    port = server.add_insecure_port("127.0.0.1:0")
    server.start()
    with grpc.insecure_channel(f"127.0.0.1:{port}") as channel:
        yield servicer, service_grpc.BasiliskSimulationServiceStub(channel)
    server.stop(None)


def configure_request():
    """The P0 default without sensors, which BSK-4 (#17) to BSK-6 (#19) add."""
    request = p0_default_request()
    del request.configuration.sensors[:]
    return request


def step_request(sequence, run_id="p0-default", sim_time_ns=None):
    return service_pb.StepRequest(
        run_id=run_id,
        sequence=sequence,
        sim_time_ns=sequence * STEP_NS if sim_time_ns is None else sim_time_ns,
        command=zero_command(),
    )


def status_of(call, request):
    with pytest.raises(grpc.RpcError) as error:
        call(request)
    return error.value.code(), error.value.details()


def rows(matrix):
    return [[row.x, row.y, row.z] for row in (matrix.row0, matrix.row1, matrix.row2)]


def xyz(vector):
    return [vector.x, vector.y, vector.z]


def test_quaternion_rules():
    """README §4: Argus (x, y, z, w) is Basilisk [w, x, y, z]; EP2MRP needs a NumPy array."""
    half = np.sqrt(0.5)
    assert np.allclose(rbk.EP2C(np.array([half, 0, 0, half])), [[0, 1, 0], [-1, 0, 0], [0, 0, 1]], atol=1e-15)
    assert np.allclose(rbk.EP2C(np.array([0.5, 0.5, 0.5, 0.5]))[2], [1, 0, 0], atol=1e-15)
    assert np.allclose(rbk.EP2MRP(np.array([-0.5, 0.5, 0.5, 0.5])), [-1 / 3, -1 / 3, -1 / 3], atol=1e-15)


def test_calls_need_a_configured_run(service):
    _, stub = service
    assert status_of(stub.Step, step_request(0))[0] == grpc.StatusCode.FAILED_PRECONDITION
    assert status_of(stub.Reset, service_pb.ResetRequest(run_id="p0-default"))[0] == (
        grpc.StatusCode.FAILED_PRECONDITION)


def test_configure_run_rejects_before_building(service):
    _, stub = service
    # Every sensor kind is still UNIMPLEMENTED.
    assert status_of(stub.ConfigureRun, p0_default_request())[0] == grpc.StatusCode.UNIMPLEMENTED

    unknown = configure_request()
    unknown.configuration.kernel_set_id = "no-such-set"
    code, details = status_of(stub.ConfigureRun, unknown)
    assert code == grpc.StatusCode.NOT_FOUND
    assert "kernel_sets/no-such-set.json" in details

    # An invalid field outranks the missing kernel set.
    unknown.configuration.spacecraft.mass_kg = 0.0
    assert status_of(stub.ConfigureRun, unknown)[0] == grpc.StatusCode.INVALID_ARGUMENT


def test_kernel_load_failure_is_failed_precondition(service, monkeypatch, tmp_path):
    _, stub = service
    window = KernelCoverage(*(datetime(year, 1, 1, tzinfo=timezone.utc) for year in (2000, 2030, 2026)))
    vanished = KernelSet(P0_KERNEL_SET, (tmp_path / "vanished.bsp",), window, {})
    monkeypatch.setattr(servicer_module, "load_kernel_set", lambda kernel_set_id, data_dir=None: vanished)
    code, details = status_of(stub.ConfigureRun, configure_request())
    assert code == grpc.StatusCode.FAILED_PRECONDITION
    assert "vanished.bsp" in details


def test_configure_run_echoes_sensor_ids_in_request_order(service, monkeypatch, tmp_path):
    """Sensor IDs live in SensorConfiguration.definition; checked before any kind is built."""
    _, stub = service
    window = KernelCoverage(*(datetime(year, 1, 1, tzinfo=timezone.utc) for year in (2000, 2030, 2026)))
    kernel_set = KernelSet(P0_KERNEL_SET, (tmp_path / "unused.bsp",), window, {})
    monkeypatch.setattr(servicer_module, "load_kernel_set", lambda kernel_set_id, data_dir=None: kernel_set)
    monkeypatch.setattr(servicer_module, "SUPPORTED_SENSOR_KINDS", frozenset({"imu", "magnetometer", "light_sensor"}))
    monkeypatch.setattr(servicer_module, "Scenario", lambda configuration, kernel_set: SimpleNamespace(
        spice_earth_frame="ITRF93"))
    request = p0_default_request()
    response = stub.ConfigureRun(request)
    assert list(response.configured_sensor_ids) == [
        sensor.definition.sensor_id for sensor in request.configuration.sensors]
    assert response.configured_sensor_ids[:2] == ["imu.main", "mag.main"]


def test_concurrent_call_is_resource_exhausted(service):
    servicer, stub = service
    with servicer._lock:
        assert status_of(stub.Step, step_request(0))[0] == grpc.StatusCode.RESOURCE_EXHAUSTED


def test_building_a_scenario_never_downloads(tmp_path):
    """bsk 2.11.1 fetches de430 through pooch when SpiceInterface is built with defaults (D3)."""
    script = """
import sys
from types import SimpleNamespace
sys.path[:0] = [".", "tests"]
attempts = []
def audit(event, args):
    if event == "socket.connect":
        attempts.append(args[1])
        raise OSError("network blocked")
sys.addaudithook(audit)
from datetime import datetime, timezone
from pathlib import Path
from argus_basilisk.kernels import KernelCoverage, KernelSet
from argus_basilisk.scenario import KernelLoadError, Scenario
from test_validation import p0_default_request
window = KernelCoverage(*(datetime(year, 1, 1, tzinfo=timezone.utc) for year in (2000, 2030, 2026)))
vanished = KernelSet("x", (Path(sys.argv[1]) / "vanished.bsp",), window, {})
try:
    Scenario(p0_default_request().configuration, vanished)
except KernelLoadError:
    pass
else:
    raise AssertionError("expected KernelLoadError")
assert not attempts, attempts
"""
    cache = tmp_path / "bsk_support_data"
    environment = dict(os.environ, BSK_SUPPORT_DATA_CACHE=str(cache))
    subprocess.run([sys.executable, "-c", script, str(tmp_path)], cwd=ROOT, env=environment, check=True)
    assert not cache.exists() or not any(path.is_file() for path in cache.rglob("*"))


# --- A live run against the P0 kernel set ---------------------------------------------------


@real_kernels
def test_configure_run_echoes_every_field(service):
    servicer, stub = service
    response = stub.ConfigureRun(configure_request())
    assert response.run_id == "p0-default"
    assert response.kernel_set_id == P0_KERNEL_SET
    assert response.fixed_step_ns == STEP_NS
    assert list(response.configured_sensor_ids) == []
    assert response.basilisk_version == "2.11.1"
    assert response.spice_earth_frame == "ITRF93"
    assert servicer.state == servicer_module.READY


@real_kernels
def test_step_checks_run_id_sequence_time_command_and_coverage(service):
    servicer, stub = service
    stub.ConfigureRun(configure_request())
    assert status_of(stub.Step, step_request(0, run_id="other"))[0] == grpc.StatusCode.FAILED_PRECONDITION
    code, details = status_of(stub.Step, step_request(1))
    assert code == grpc.StatusCode.FAILED_PRECONDITION
    assert "expected 0" in details
    assert status_of(stub.Step, step_request(0, sim_time_ns=1))[0] == grpc.StatusCode.INVALID_ARGUMENT
    unset = step_request(0)
    unset.ClearField("command")
    assert status_of(stub.Step, unset)[0] == grpc.StatusCode.INVALID_ARGUMENT
    pushing = step_request(0)
    pushing.command.thruster_force_body_n.x = 1.0
    assert status_of(stub.Step, pushing)[0] == grpc.StatusCode.UNIMPLEMENTED
    assert stub.Step(step_request(0)).sequence == 0
    assert servicer.state == servicer_module.RUNNING

    # Configured 50 ms before coverage ends: Step(1) is outside it and must not execute, since
    # CSPICE aborts the process outside coverage.
    at_end = configure_request()
    at_end.configuration.epoch_utc.FromDatetime(datetime(2026, 8, 5, 23, 59, 59, 950_000, tzinfo=timezone.utc))
    stub.ConfigureRun(at_end)
    stub.Step(step_request(0))
    assert status_of(stub.Step, step_request(1))[0] == grpc.StatusCode.OUT_OF_RANGE


@real_kernels
def test_steps_repeat_bit_for_bit_after_reset(service):
    _, stub = service
    stub.ConfigureRun(configure_request())

    def run():
        return [stub.Step(step_request(n)) for n in range(100)]

    first = run()
    assert [response.sim_time_ns for response in first] == [n * STEP_NS for n in range(100)]
    assert stub.Reset(service_pb.ResetRequest(run_id="p0-default")).run_id == "p0-default"
    second = run()
    assert [r.SerializeToString(deterministic=True) for r in first] == [
        r.SerializeToString(deterministic=True) for r in second]


@reference_kernels
def test_step_zero_matches_the_spice_reference(service):
    _, stub = service
    request = configure_request()
    request.configuration.kernel_set_id = REFERENCE_KERNEL_SET
    stub.ConfigureRun(request)
    response = stub.Step(step_request(0))

    earth, sun = response.earth, response.sun
    assert (earth.planet_name, sun.planet_name) == ("earth", "sun")
    assert earth.orientation_computed
    assert np.abs(np.array(rows(earth.j2000_to_planet_fixed)) - J2000_TO_ITRF93).max() <= 1e-12
    assert np.abs(np.array(rows(earth.j2000_to_planet_fixed_rate_per_s)) - J2000_TO_ITRF93_RATE).max() <= 1e-12
    sun_from_earth = np.array(xyz(sun.position_inertial_m)) - xyz(earth.position_inertial_m)
    assert np.abs(sun_from_earth - SUN_FROM_EARTH_J2000_M).max() <= 1e-3
    assert response.HasField("spacecraft_shadow_factor")
    assert response.pacing_overrun_count == 0


@real_kernels
def test_stale_message_is_internal_and_fails_the_run(service):
    servicer, stub = service
    stub.ConfigureRun(configure_request())
    never_written = messaging.EclipseMsg()
    reader = messaging.EclipseMsgReader()
    reader.subscribeTo(never_written)
    servicer._run.scenario.readers["eclipse.eclipseOutMsgs[0]"] = reader

    code, details = status_of(stub.Step, step_request(0))
    assert code == grpc.StatusCode.INTERNAL
    assert "eclipse.eclipseOutMsgs[0]" in details
    assert servicer.state == servicer_module.FAILED
    assert status_of(stub.Step, step_request(0))[0] == grpc.StatusCode.FAILED_PRECONDITION

    stub.Reset(service_pb.ResetRequest(run_id="p0-default"))
    assert stub.Step(step_request(0)).sim_time_ns == 0
