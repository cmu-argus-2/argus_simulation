"""The device kind registry (D11; docs/device-model.md §7, README §9).

Each Basilisk-modelled kind is one module in this package, registered by name in KINDS. A kind
module defines:

- KIND: its name, the DeviceConfiguration.kind that selects it;
- PARAMETERS: each parameter key and its DeviceValue type (_common.NUMBER, VECTOR3, ...). Every
  key is required;
- OUTPUTS: each key and type of its BasiliskDeviceSample.output;
- check(values, invalid): its range checks. values maps each parameter key that has its declared
  type and is finite to its Python value (a float, or a tuple of components); a problem is
  reported as invalid(key, reason, value);
- build(device, random_seed, inputs): its Argus sensor from a validated DeviceConfiguration and
  the _common.Inputs messages. The sensor has device_id, sample_period_ns, modules, readers,
  ready and output(), which returns the output map.

validation.py and the scenario walk KINDS instead of switching on kinds, so adding a kind is one
module plus one entry here. Kind modules import numpy and Basilisk only inside their builders,
so validation runs without the basilisk extra.
"""

from . import imu, light_sensor, magnetometer
from ._common import Inputs

__all__ = ["KINDS", "build_sensors", "of_kind"]

KINDS = {module.KIND: module for module in (imu, magnetometer, light_sensor)}


def of_kind(devices, kind: str) -> list:
    """The devices of one kind, in configuration order."""
    return [device for device in devices if device.kind == kind]


def build_sensors(
        configuration, spacecraft_state_msg, magnetic_field_msg=None, *,
        sun_msg=None, eclipse_msg=None, albedo_msgs=None) -> list:
    """The Argus sensors of a validated configuration, in configuration order.

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
        built.append(kind.build(device, configuration.random_seed, inputs))
    return built
