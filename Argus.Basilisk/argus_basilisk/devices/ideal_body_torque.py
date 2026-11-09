"""The ideal body torque kind: a torque applied directly to the body (D11, CON-2 #34).

The kind for a spacecraft whose actuators aren't known yet. BSK-8 (#21) builds it as Basilisk's
extForceTorque; until then the service builds nothing, so only idle commands are accepted.
"""

from ._common import ACTUATOR, TEXT, VECTOR3, Invalid, limit_policy, positive_components

KIND = "ideal_body_torque"
ROLE = ACTUATOR

PARAMETERS = {
    # |torque_body_n_m| per body axis.
    "max_torque_body_n_m": VECTOR3,
    "limit_policy": TEXT,
}

COMMANDS = {
    "torque_body_n_m": VECTOR3,
}

# What the actuator runs in a step whose command leaves it out: no torque.
IDLE = {
    "torque_body_n_m": (0.0, 0.0, 0.0),
}


def check(values, invalid: Invalid) -> None:
    positive_components(values, invalid, "max_torque_body_n_m")
    limit_policy(values, invalid)
