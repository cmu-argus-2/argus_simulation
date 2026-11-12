"""The magnetic torquer kind: a dipole along a body-fixed axis (D11, CON-2 #34).

All of a run's magnetic torquers are one Basilisk MtbEffector, one column of GtMatrix_B each in
configuration order, on the MagneticFieldWMM field (README §9 "Actuators", BSK-8 #21). DATA-2
(#68) chose this fidelity for P0's six coils: torque m x B with the true field, an ideal dipole
and no coil dynamics.
"""

from ._common import ACTUATOR, NUMBER, TEXT, VECTOR3, ActuatorGroup, Invalid, limit_policy, positive, unit_axis

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

# bsk MAX_EFF_CNT: MTBArrayConfigMsgPayload and MTBCmdMsgPayload hold at most this many torquers.
MAX_DEVICES = 36


def check(values, invalid: Invalid) -> None:
    unit_axis(values, invalid, "dipole_axis_body")
    positive(values, invalid, "max_dipole_a_m2")
    limit_policy(values, invalid)


def check_limits(values, parameters, invalid: Invalid) -> None:
    # MtbEffector would clamp silently, so the gateway's limit_policy (CORE-10, #46) is the only
    # one, and a command past the limit is refused here.
    dipole = values.get("dipole_a_m2")
    limit = parameters.get("max_dipole_a_m2")
    if dipole is None or limit is None:
        return
    if abs(dipole) > limit:
        invalid("dipole_a_m2", f"must be within +-max_dipole_a_m2 ({limit!r})", dipole)


def build(devices, inputs) -> ActuatorGroup:
    """One MtbEffector for every torquer. It reads its command, the field and its configuration in
    computeForceTorque, at each integration substep, so the command written before Step(n) acts over
    [t_n-1, t_n) on the field MagneticFieldWMM wrote at t_n-1 (§6)."""
    from Basilisk.architecture import messaging
    from Basilisk.simulation import MtbEffector

    if inputs.magnetic_field_msg is None:
        raise ValueError("magnetic torquers need the MagneticFieldWMM output")
    count = len(devices)

    configuration = messaging.MTBArrayConfigMsgPayload()
    configuration.numMTB = count
    # GtMatrix_B is 3 x numMTB, row-major: column j is torquer j's dipole axis.
    axes = [device.parameters["dipole_axis_body"].vector3 for device in devices]
    configuration.GtMatrix_B = [getattr(axis, row) for row in "xyz" for axis in axes]
    configuration.maxMtbDipoles = [device.parameters["max_dipole_a_m2"].number for device in devices]
    configuration_msg = messaging.MTBArrayConfigMsg().write(configuration)

    command_msg = messaging.MTBCmdMsg()
    command_msg.write(_payload([IDLE["dipole_a_m2"]] * count), 0)

    effector = MtbEffector.MtbEffector()
    effector.ModelTag = "mtbEffector"
    effector.mtbCmdInMsg.subscribeTo(command_msg)
    effector.magInMsg.subscribeTo(inputs.magnetic_field_msg)
    effector.mtbParamsInMsg.subscribeTo(configuration_msg)

    device_ids = tuple(device.device_id for device in devices)

    def write(commands, time_ns: int) -> None:
        command_msg.write(_payload([commands[device_id]["dipole_a_m2"] for device_id in device_ids]), time_ns)

    return ActuatorGroup(
        device_ids=device_ids, modules=(effector,), effectors=(effector,), write=write,
        keep=(configuration_msg, command_msg))


def _payload(dipoles):
    from Basilisk.architecture import messaging

    payload = messaging.MTBCmdMsgPayload()
    payload.mtbDipoleCmds = list(dipoles)
    return payload
