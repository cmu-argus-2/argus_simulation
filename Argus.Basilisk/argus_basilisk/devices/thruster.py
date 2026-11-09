"""The thruster kind: a force along a body-fixed direction, at the device's mount position
(D11, CON-2 #34).

BSK-8 (#21) builds it as Basilisk's thrusterDynamicEffector (thrLoc_B is mount.position_body_m),
converting thrust_n to an on-time of thrust_n / max_thrust_n of the step; until then the service
builds nothing, so only idle commands are accepted.
"""

from ._common import ACTUATOR, NUMBER, TEXT, VECTOR3, Invalid, limit_policy, positive, unit_axis

KIND = "thruster"
ROLE = ACTUATOR

PARAMETERS = {
    # thrDir_B: the direction of the force on the body.
    "thrust_direction_body": VECTOR3,
    # MaxThrust.
    "max_thrust_n": NUMBER,
    "limit_policy": TEXT,
}

COMMANDS = {
    # Held over the step, from 0 to max_thrust_n.
    "thrust_n": NUMBER,
}

IDLE = {
    "thrust_n": 0.0,
}


def check(values, invalid: Invalid) -> None:
    unit_axis(values, invalid, "thrust_direction_body")
    positive(values, invalid, "max_thrust_n")
    limit_policy(values, invalid)
