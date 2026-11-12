"""The momentum wheel kind: a wheel spun by its motor about a body-fixed axis (D11, CON-2 #34).

Basilisk would model it as reactionWheelStateEffector. P0 carries no wheel (DATA-2, #68), so the
kind has no builder and ConfigureRun returns UNIMPLEMENTED for it (README §9 "Actuators").
"""

from ._common import ACTUATOR, NUMBER, TEXT, VECTOR3, Invalid, limit_policy, positive, unit_axis

KIND = "momentum_wheel"
ROLE = ACTUATOR

PARAMETERS = {
    # gsHat_B.
    "spin_axis_body": VECTOR3,
    # Js.
    "spin_inertia_kg_m2": NUMBER,
    # u_max.
    "max_torque_n_m": NUMBER,
    # Omega_max.
    "max_speed_rad_per_s": NUMBER,
    "limit_policy": TEXT,
}

# Omega at t = 0, about spin_axis_body.
STATE = {
    "speed_rad_per_s": NUMBER,
}

COMMANDS = {
    # The motor torque on the wheel about spin_axis_body; the body feels its reaction.
    "motor_torque_n_m": NUMBER,
}

IDLE = {
    "motor_torque_n_m": 0.0,
}


def check(values, invalid: Invalid) -> None:
    unit_axis(values, invalid, "spin_axis_body")
    positive(values, invalid, "spin_inertia_kg_m2")
    positive(values, invalid, "max_torque_n_m")
    positive(values, invalid, "max_speed_rad_per_s")
    limit_policy(values, invalid)
