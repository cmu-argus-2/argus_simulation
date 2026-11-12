"""The device kind registry (D11; docs/device-model.md §7, README §9).

Each Basilisk-modelled kind is one module in this package, registered by name in KINDS. A kind
module defines:

- KIND: its name, the DeviceConfiguration.kind that selects it;
- ROLE (optional): ACTUATOR for an actuator kind; a module without it is a sensor kind;
- PARAMETERS: each parameter key and its DeviceValue type (_common.NUMBER, VECTOR3, ...). Every
  key is required;
- STATE (optional): each initial-state key and its type, as PARAMETERS; none when it is absent,
  as for every sensor kind so far;
- OUTPUTS (sensors): each key and type of its BasiliskDeviceSample.output;
- COMMANDS and IDLE (actuators): each command key and its type, and the values the actuator runs
  in a step whose command leaves it out (no output); check_command(values, invalid) (optional),
  a command value the kind can never take, and check_limits(values, parameters, invalid), a
  command past the device's own limits, both reported as check() reports;
- check(values, invalid): its range checks. values maps each parameter key that has its declared
  type and is finite to its Python value (a float, a str, or a tuple of components); a problem is
  reported as invalid(key, reason, value);
- build(device, random_seed, inputs) (sensors): its Argus sensor from a validated
  DeviceConfiguration and the _common.Inputs messages. The sensor has device_id,
  sample_period_ns, modules, readers, ready and output(), which returns the output map;
- build(devices, inputs) (actuators): one _common.ActuatorGroup for all of the run's devices of
  the kind, in configuration order (BSK-8, #21). An actuator kind without build is registered but
  not built: ConfigureRun returns UNIMPLEMENTED for it;
- MAX_DEVICES (optional): the most devices of the kind one run may configure.

validation.py and the scenario walk KINDS instead of switching on kinds, so adding a kind is one
module plus one entry here. Kind modules import numpy and Basilisk only inside their builders,
so validation runs without the basilisk extra.
"""

from . import ideal_body_torque, imu, light_sensor, magnetic_torquer, magnetometer, momentum_wheel, thruster
from ._common import ACTUATOR, SENSOR, Inputs

__all__ = [
    "ACTUATOR", "KINDS", "SENSOR", "actuators", "build_actuators", "build_sensors", "is_actuator", "is_built",
    "needs_magnetic_field", "of_kind", "state_keys"]

KINDS = {
    module.KIND: module
    for module in (
        imu, magnetometer, light_sensor,
        ideal_body_torque, momentum_wheel, magnetic_torquer, thruster,
    )
}


def state_keys(kind) -> dict:
    """A kind module's initial-state keys and types: its STATE, or none."""
    return getattr(kind, "STATE", {})


def is_actuator(kind) -> bool:
    """Whether a kind module is an actuator kind."""
    return getattr(kind, "ROLE", SENSOR) == ACTUATOR


def is_built(kind) -> bool:
    """Whether the service builds a kind module's devices; every sensor kind is built."""
    return hasattr(kind, "build")


def needs_magnetic_field(configuration) -> bool:
    """Whether the run builds MagneticFieldWMM: a magnetometer reads it, and a magnetic torquer
    pushes against it. Its WMM2025 validity then bounds the run (README §3)."""
    return bool(of_kind(configuration.devices, magnetometer.KIND) or of_kind(configuration.devices, magnetic_torquer.KIND))


def actuators(configuration) -> dict:
    """The run's actuators by device ID, each to its kind module: the only devices a Step command
    may name."""
    return {
        device.device_id: KINDS[device.kind] for device in configuration.devices
        if is_actuator(KINDS.get(device.kind))}


def of_kind(devices, kind: str) -> list:
    """The devices of one kind, in configuration order."""
    return [device for device in devices if device.kind == kind]


def build_sensors(
        configuration, spacecraft_state_msg, magnetic_field_msg=None, *,
        sun_msg=None, eclipse_msg=None, albedo_msgs=None) -> list:
    """The Argus sensors of a validated configuration, in configuration order; actuators are
    build_actuators'.

    magnetic_field_msg is the MagneticFieldWMM output for the spacecraft, required with a
    magnetometer. Light sensors need the SPICE Sun, the eclipse output and their albedo output,
    albedo_msgs[device_id].
    """
    inputs = Inputs(
        spacecraft_state_msg=spacecraft_state_msg,
        magnetic_field_msg=magnetic_field_msg,
        sun_msg=sun_msg,
        eclipse_msg=eclipse_msg,
        albedo_msgs=albedo_msgs or {},
    )
    built = []
    for device in configuration.devices:
        kind = KINDS.get(device.kind)
        if kind is None:
            raise ValueError(f"device kind {device.kind!r} is not registered (device_id {device.device_id!r})")
        if is_actuator(kind):
            continue
        built.append(kind.build(device, configuration.random_seed, inputs))
    return built


def build_actuators(configuration, spacecraft_state_msg, magnetic_field_msg=None) -> list:
    """One ActuatorGroup per actuator kind the validated configuration uses, in the order each
    kind first appears. magnetic_field_msg is the MagneticFieldWMM output, required with a
    magnetic torquer."""
    inputs = Inputs(spacecraft_state_msg=spacecraft_state_msg, magnetic_field_msg=magnetic_field_msg)
    by_kind = {}
    for device in configuration.devices:
        kind = KINDS.get(device.kind)
        if is_actuator(kind):
            by_kind.setdefault(device.kind, []).append(device)
    groups = []
    for name, members in by_kind.items():
        kind = KINDS[name]
        if not is_built(kind):
            raise ValueError(f"actuator kind {name!r} is not built (device_id {members[0].device_id!r})")
        groups.append(kind.build(members, inputs))
    return groups
