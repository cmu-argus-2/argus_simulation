"""Basilisk simulation build and stepping (README §4 to §8).

Build order steps 1 and 2 (README §12): spacecraft, SPICE, eclipse and the IMU. The magnetometer
and light sensors (§9), pacing (§5) and actuators (G1) are rejected by validation before a
Scenario is built.

The servicer builds a Scenario only after validation (BSK-2) and kernel verification (BSK-1)
pass. Each Scenario owns a fresh SimBaseClass and fresh modules; Reset builds a new one.
"""

from datetime import datetime, timezone
import math

import numpy as np

from Basilisk import __version__ as BASILISK_VERSION
from Basilisk.architecture import astroConstants
from Basilisk.architecture.bskLogging import BasiliskError
from Basilisk.simulation import eclipse, gravityEffector, spacecraft, spiceInterface
from Basilisk.utilities import RigidBodyKinematics as rbk
from Basilisk.utilities import SimulationBaseClass, orbitalMotion

from .contracts import service_pb, types_pb
from .kernels import KernelSet
from .sensors import build_sensors

__all__ = ["BASILISK_VERSION", "KernelLoadError", "Scenario", "StaleMessageError", "format_epoch"]

# §8: gravity-body order; the empty Sun frame still auto-detects IAU_SUN.
PLANET_NAMES = ("earth", "sun")
# (mu m^3/s^2, equatorial radius m), as simIncludeGravBody.BODY_DATA sets them. That module is not
# imported: it imports dataFetcher, which sends a request to github.com at import time.
GRAVITY = {
    "earth": (astroConstants.MU_EARTH * 1e9, astroConstants.REQ_EARTH * 1e3),
    "sun": (astroConstants.MU_SUN * 1e9, astroConstants.REQ_SUN * 1e3),
}
PLANET_FRAMES = ("ITRF93", "")
EARTH_INDEX = 0
SUN_INDEX = 1

PROCESS_NAME = "dynamics"
TASK_NAME = "dynamics"
# §6: the dynamics task runs before every sensor task at the same t_n. One sensor task per
# distinct sample period, named sensors_<ns>.
DYNAMICS_TASK_PRIORITY = 200
SENSOR_TASK_PRIORITY = 100
# §6 tick order within the task: higher priority runs first.
SPICE_PRIORITY = 300
SPACECRAFT_PRIORITY = 200
ECLIPSE_PRIORITY = 100


class KernelLoadError(Exception):
    """SPICE rejected a verified kernel during InitializeSimulation (FAILED_PRECONDITION)."""


class StaleMessageError(Exception):
    """A returned message was not written at t_n, or holds a non-finite value (INTERNAL)."""


def format_epoch(timestamp) -> str:
    """Format a google.protobuf.Timestamp for UTCCalInit as yyyy-MM-ddTHH:mm:ss.fffffffffZ (§4).

    Seconds and nanos are formatted separately, never through a microsecond datetime.
    """
    whole = datetime.fromtimestamp(timestamp.seconds, tz=timezone.utc)
    return f"{whole:%Y-%m-%dT%H:%M:%S}.{timestamp.nanos:09d}Z"


class Scenario:
    """One built and initialized Basilisk simulation for a validated RunConfiguration."""

    def __init__(self, configuration, kernel_set: KernelSet):
        """Build every module from configuration and call InitializeSimulation (§3 step 3).

        Raises:
            KernelLoadError: SPICE could not load a kernel.
        """
        self.fixed_step_ns = configuration.fixed_step_ns
        self.sim = SimulationBaseClass.SimBaseClass()
        process = self.sim.CreateNewProcess(PROCESS_NAME)
        process.addTask(self.sim.CreateNewTask(TASK_NAME, self.fixed_step_ns), DYNAMICS_TASK_PRIORITY)

        # SPICE (§8). auto_configure_kernels=False stops bsk 2.11.1 from fetching its default
        # kernels (de430 and others) through pooch when the module is constructed.
        self.spice = spiceInterface.SpiceInterface(auto_configure_kernels=False)
        self.spice.ModelTag = "spiceInterface"
        self.spice.clearKernelPaths()
        self.spice.addKernelPaths([str(path) for path in kernel_set.kernel_paths])
        self.spice.addPlanetNames(list(PLANET_NAMES))
        self.spice.planetFrames = list(PLANET_FRAMES)
        self.spice.referenceBase = "j2000"
        self.spice.zeroBase = "Earth"
        self.spice.UTCCalInit = format_epoch(configuration.epoch_utc)
        self.sim.AddModelToTask(TASK_NAME, self.spice, SPICE_PRIORITY)

        # Gravity bodies in PLANET_NAMES order, each fed by its SPICE planet state; Earth central.
        self._gravity_bodies = [
            _gravity_body(name, self.spice.planetStateOutMsgs[index]) for index, name in enumerate(PLANET_NAMES)]
        earth = self._gravity_bodies[EARTH_INDEX]
        earth.isCentralBody = True

        # Spacecraft hub (§4).
        self.spacecraft = spacecraft.Spacecraft()
        self.spacecraft.ModelTag = "spacecraft"
        _configure_hub(self.spacecraft.hub, configuration, earth.mu)
        self.spacecraft.gravField.setGravBodies(gravityEffector.GravBodyVector(self._gravity_bodies))
        self.sim.AddModelToTask(TASK_NAME, self.spacecraft, SPACECRAFT_PRIORITY)

        # Eclipse from the SPICE Sun and Earth (§8).
        self.eclipse = eclipse.Eclipse()
        self.eclipse.ModelTag = "eclipse"
        self.eclipse.sunInMsg.subscribeTo(self.spice.planetStateOutMsgs[SUN_INDEX])
        self.eclipse.addPlanetToModel(self.spice.planetStateOutMsgs[EARTH_INDEX])
        self.eclipse.addSpacecraftToModel(self.spacecraft.scStateOutMsg)
        self.sim.AddModelToTask(TASK_NAME, self.eclipse, ECLIPSE_PRIORITY)

        # Sensors (§9), each on the task for its sample period, after dynamics.
        self.sensors = build_sensors(configuration, self.spacecraft.scStateOutMsg)
        for period_ns in sorted({sensor.sample_period_ns for sensor in self.sensors}):
            process.addTask(self.sim.CreateNewTask(f"sensors_{period_ns}", period_ns), SENSOR_TASK_PRIORITY)
        for sensor in self.sensors:
            for module in sensor.modules:
                self.sim.AddModelToTask(f"sensors_{sensor.sample_period_ns}", module)

        # Readers subscribed at build time (§6); only readers expose isWritten/timeWritten.
        self.readers = {
            "spacecraft.scStateOutMsg": self.spacecraft.scStateOutMsg.addSubscriber(),
            "spiceInterface.planetStateOutMsgs[earth]":
                self.spice.planetStateOutMsgs[EARTH_INDEX].addSubscriber(),
            "spiceInterface.planetStateOutMsgs[sun]":
                self.spice.planetStateOutMsgs[SUN_INDEX].addSubscriber(),
            "eclipse.eclipseOutMsgs[0]": self.eclipse.eclipseOutMsgs[0].addSubscriber(),
        }

        try:
            self.sim.InitializeSimulation()
        except BasiliskError as error:
            raise KernelLoadError(f"SPICE could not load kernel set {kernel_set.kernel_set_id!r}: {error}") from error

    @property
    def spice_earth_frame(self) -> str:
        """The Earth frame read back from spiceInterface.planetFrames."""
        return self.spice.planetFrames[EARTH_INDEX]

    def step(self, sequence: int) -> service_pb.StepResponse:
        """Run to t_n = sequence x fixed_step_ns and return the state there (§3 Step 5 to 7).

        The caller has checked sequence, time, command and coverage, and fills the echoes.

        Raises:
            StaleMessageError: a returned message was stale or non-finite.
        """
        t_n = sequence * self.fixed_step_ns
        # A sensor that has not run before t_n runs without data at t_n (imuSensor's first firing).
        was_ready = [sensor.ready for sensor in self.sensors]
        self.sim.ConfigureStopTime(t_n)
        self.sim.ExecuteSimulation()
        # Step 6 (write command n into the actuator inputs) starts with actuators (G1); every
        # command is zero until then.

        if self.sim.TotalSim.CurrentNanos != t_n:
            raise StaleMessageError(f"CurrentSimNanos is {self.sim.TotalSim.CurrentNanos}, not t_n = {t_n}")
        for name, reader in self.readers.items():
            _check_fresh(name, reader, t_n)

        state = self.readers["spacecraft.scStateOutMsg"]()
        earth = self.readers["spiceInterface.planetStateOutMsgs[earth]"]()
        sun = self.readers["spiceInterface.planetStateOutMsgs[sun]"]()
        shadow = self.readers["eclipse.eclipseOutMsgs[0]"]()

        return service_pb.StepResponse(
            sim_time_ns=self.sim.TotalSim.CurrentNanos,
            spacecraft=service_pb.BasiliskSpacecraftState(
                position_bn_inertial_m=_vector(state.r_BN_N, "scStateOutMsg.r_BN_N"),
                velocity_bn_inertial_m_per_s=_vector(state.v_BN_N, "scStateOutMsg.v_BN_N"),
                sigma_bn=service_pb.Mrp(**_xyz(state.sigma_BN, "scStateOutMsg.sigma_BN")),
                omega_bn_body_rad_per_s=_vector(state.omega_BN_B, "scStateOutMsg.omega_BN_B"),
            ),
            earth=_planet(earth, "earth"),
            sun=_planet(sun, "sun"),
            # bsk 2.11.1: the reader returns the eclipse module's EclipseMsgPayload, whose only
            # field is the C name shadowFactor (1 sunlit, 0 umbra) and reads without a warning;
            # illuminationFactor is a Python alias that exists only on messaging.EclipseMsgPayload.
            spacecraft_shadow_factor=_finite(shadow.shadowFactor, "eclipseOutMsgs[0].shadowFactor"),
            sensor_samples=[
                self._sample(sensor, ready, t_n)
                for sensor, ready in zip(self.sensors, was_ready)
                if t_n % sensor.sample_period_ns == 0
            ],
            pacing_overrun_count=0,
        )

    def _sample(self, sensor, was_ready: bool, t_n: int) -> service_pb.BasiliskSensorSample:
        """The sample of a sensor due at t_n (§7); its measurement is unset when it ran without data."""
        for name, reader in sensor.readers.items():
            _check_fresh(name, reader, t_n)
        sample = service_pb.BasiliskSensorSample(sensor_id=sensor.sensor_id, sample_time_ns=t_n)
        if was_ready:
            measurement = sensor.measurement()
            _check_finite_fields(measurement, sensor.sensor_id)
            getattr(sample, sensor.kind).CopyFrom(measurement)
        return sample


def _gravity_body(name: str, planet_state_msg) -> gravityEffector.GravBodyData:
    """A point-mass gravity body, as simIncludeGravBody.gravBodyFactory.createBody builds it."""
    body = gravityEffector.GravBodyData()
    body.planetName = f"{name}_planet_data"
    body.displayName = name
    body.modelDictionaryKey = ""
    body.mu, body.radEquator = GRAVITY[name]
    body.radiusRatio = 1.0
    body.planetBodyInMsg.subscribeTo(planet_state_msg)
    return body


def _configure_hub(hub, configuration, mu: float) -> None:
    orbit = configuration.initial_orbit
    elements = orbitalMotion.ClassicElements()
    elements.a = orbit.semi_major_axis_m
    elements.e = orbit.eccentricity
    elements.i = math.radians(orbit.inclination_deg)
    elements.Omega = math.radians(orbit.raan_deg)
    elements.omega = math.radians(orbit.argument_of_periapsis_deg)
    elements.f = math.radians(orbit.true_anomaly_deg)
    position, velocity = orbitalMotion.elem2rv(mu, elements)

    body = configuration.spacecraft
    inertia = body.inertia_body_kg_m2
    quaternion = body.initial_body_to_j2000
    rate = body.initial_angular_velocity_body_rad_per_s

    hub.mHub = body.mass_kg
    hub.r_BcB_B = [[0.0], [0.0], [0.0]]
    # Matrix3 is row-major: row0 is the first row.
    hub.IHubPntBc_B = [[row.x, row.y, row.z] for row in (inertia.row0, inertia.row1, inertia.row2)]
    hub.r_CN_NInit = np.asarray(position).reshape(3, 1)
    hub.v_CN_NInit = np.asarray(velocity).reshape(3, 1)
    # §4: Argus body_to_j2000 (x, y, z, w) is the Euler-parameter set [w, x, y, z] of B relative
    # to N. A NumPy array, because EP2MRP negates its argument when w < 0.
    sigma = rbk.EP2MRP(np.array([quaternion.w, quaternion.x, quaternion.y, quaternion.z]))
    hub.sigma_BNInit = np.asarray(sigma).reshape(3, 1)
    hub.omega_BN_BInit = [[rate.x], [rate.y], [rate.z]]


def _check_fresh(name: str, reader, t_n: int) -> None:
    if not reader.isWritten():
        raise StaleMessageError(f"{name} was never written (t_n = {t_n} ns)")
    if reader.timeWritten() != t_n:
        raise StaleMessageError(f"{name} was written at {reader.timeWritten()} ns, not t_n = {t_n} ns")


def _check_finite_fields(message, name: str) -> None:
    for field, value in message.ListFields():
        path = f"{name}.{field.name}"
        if field.message_type is not None:
            _check_finite_fields(value, path)
        else:
            _finite(value, path)


def _finite(value: float, name: str) -> float:
    if not math.isfinite(value):
        raise StaleMessageError(f"{name} is not finite: {value!r}")
    return value


def _xyz(values, name: str) -> dict:
    x, y, z = (_finite(value, name) for value in values)
    return {"x": x, "y": y, "z": z}


def _vector(values, name: str):
    return types_pb.Vector3(**_xyz(values, name))


def _matrix(rows, name: str):
    row0, row1, row2 = (types_pb.Vector3(**_xyz(row, name)) for row in rows)
    return types_pb.Matrix3(row0=row0, row1=row1, row2=row2)


def _planet(payload, name: str) -> service_pb.BasiliskPlanetState:
    prefix = f"planetStateOutMsgs[{name}]"
    return service_pb.BasiliskPlanetState(
        planet_name=payload.PlanetName,
        position_inertial_m=_vector(payload.PositionVector, f"{prefix}.PositionVector"),
        velocity_inertial_m_per_s=_vector(payload.VelocityVector, f"{prefix}.VelocityVector"),
        j2000_to_planet_fixed=_matrix(payload.J20002Pfix, f"{prefix}.J20002Pfix"),
        j2000_to_planet_fixed_rate_per_s=_matrix(payload.J20002Pfix_dot, f"{prefix}.J20002Pfix_dot"),
        orientation_computed=payload.computeOrient != 0,
    )
