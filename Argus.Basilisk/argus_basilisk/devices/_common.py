"""Shared declarations and helpers of the device kinds (README §9).

Nothing here imports numpy or Basilisk at module level: validation.py walks the registry without
the basilisk extra, so kind modules import them only inside their builders.
"""

from dataclasses import dataclass, field
from typing import Callable, Mapping

from ..contracts import devices_pb, types_pb

# A kind module's ROLE; a module without one is a sensor kind.
SENSOR = "sensor"
ACTUATOR = "actuator"

# DeviceValue oneof cases, the types a kind declares for each key.
NUMBER = "number"
VECTOR3 = "vector3"
QUATERNION = "quaternion"
MATRIX3 = "matrix3"
FLAG = "flag"
TEXT = "text"

# check(values, invalid) reports a problem as invalid(key, reason, value).
Invalid = Callable[[str, str, object], None]


@dataclass(frozen=True)
class Inputs:
    """The Basilisk messages a sensor kind may subscribe to. A builder raises ValueError when one
    it needs is missing."""

    spacecraft_state_msg: object
    # MagneticFieldWMM envOutMsgs[0], for magnetometers.
    magnetic_field_msg: object = None
    # The SPICE Sun and eclipseOutMsgs[0], for light sensors.
    sun_msg: object = None
    eclipse_msg: object = None
    # albedo albOutMsgs[i] by device ID, for light sensors.
    albedo_msgs: Mapping[str, object] = field(default_factory=dict)


def number_value(value: float) -> devices_pb.DeviceValue:
    return devices_pb.DeviceValue(number=value)


def vector3_value(values) -> devices_pb.DeviceValue:
    x, y, z = values
    return devices_pb.DeviceValue(vector3=types_pb.Vector3(x=x, y=y, z=z))


def array(vector):
    """A Vector3 as a NumPy 3-vector."""
    import numpy as np

    return np.array([vector.x, vector.y, vector.z])


def dcm(quaternion):
    """C = EP2C([w, x, y, z]) of an Argus (x, y, z, w) quaternion (README §4)."""
    import numpy as np
    from Basilisk.utilities import RigidBodyKinematics as rbk

    return rbk.EP2C(np.array([quaternion.w, quaternion.x, quaternion.y, quaternion.z]))


# Range checks over decoded, finite values. Each returns whether the key is present and valid; a
# key that is missing, mistyped or non-finite has been reported already and is skipped.


def non_negative(values: Mapping, invalid: Invalid, key: str) -> bool:
    value = values.get(key)
    if value is None:
        return False
    if value >= 0.0:
        return True
    invalid(key, "must be finite and >= 0", value)
    return False


def positive(values: Mapping, invalid: Invalid, key: str) -> bool:
    value = values.get(key)
    if value is None:
        return False
    if value > 0.0:
        return True
    invalid(key, "must be finite and > 0", value)
    return False


# An actuator's limit_policy: what the gateway does with a command past the device's limits
# (CORE-10, #46).
LIMIT_POLICIES = ("clamp", "reject")

# Core ActuatorKind.UnitTolerance: an axis must have unit length within this.
AXIS_UNIT_TOLERANCE = 1e-6


def limit_policy(values: Mapping, invalid: Invalid, key: str = "limit_policy") -> bool:
    value = values.get(key)
    if value is None:
        return False
    if value in LIMIT_POLICIES:
        return True
    invalid(key, f"must be one of {', '.join(LIMIT_POLICIES)}", value)
    return False


def unit_axis(values: Mapping, invalid: Invalid, key: str) -> bool:
    value = values.get(key)
    if value is None:
        return False
    norm = sum(component * component for component in value) ** 0.5
    if abs(norm - 1.0) <= AXIS_UNIT_TOLERANCE:
        return True
    invalid(key, f"must have unit length within {AXIS_UNIT_TOLERANCE} (norm {norm!r})", value)
    return False


def positive_components(values: Mapping, invalid: Invalid, key: str) -> bool:
    value = values.get(key)
    if value is None:
        return False
    if all(component > 0.0 for component in value):
        return True
    invalid(key, "each component must be finite and > 0", value)
    return False
