"""The magnetic torquer kind: a dipole along a body-fixed axis (D11, CON-2 #34).

BSK-8 (#21) builds it as Basilisk's MtbEffector on the MagneticFieldWMM field; until then the
service builds nothing, so only idle commands are accepted.
"""

from ._common import ACTUATOR, NUMBER, TEXT, VECTOR3, Invalid, limit_policy, positive, unit_axis

KIND = "magnetic_torquer"
ROLE = ACTUATOR

PARAMETERS = {
    # One column of GtMatrix_B.
    "dipole_axis_body": VECTOR3,
    # maxMtbDipoles.
    "max_dipole_a_m2": NUMBER,
    "limit_policy": TEXT,
}

COMMANDS = {
    # The signed dipole along dipole_axis_body.
    "dipole_a_m2": NUMBER,
}

IDLE = {
    "dipole_a_m2": 0.0,
}


def check(values, invalid: Invalid) -> None:
    unit_axis(values, invalid, "dipole_axis_body")
    positive(values, invalid, "max_dipole_a_m2")
    limit_policy(values, invalid)
