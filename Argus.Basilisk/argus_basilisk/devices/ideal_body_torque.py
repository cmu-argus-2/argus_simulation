"""The ideal body torque kind: a torque applied directly to the body (D11, CON-2 #34).

The kind for a spacecraft whose actuators aren't known yet. Each device is one Basilisk
extForceTorque on the hub, fed by its own CmdTorqueBodyMsg (README §9 "Actuators", BSK-8 #21).
"""

from ._common import ACTUATOR, TEXT, VECTOR3, ActuatorGroup, Invalid, limit_policy, positive_components

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


def check_limits(values, parameters, invalid: Invalid) -> None:
    # The gateway applies limit_policy before the service sees a command (CORE-10, #46), so a
    # command past the limits is refused, never clamped here.
    torque = values.get("torque_body_n_m")
    limit = parameters.get("max_torque_body_n_m")
    if torque is None or limit is None:
        return
    if any(abs(component) > bound for component, bound in zip(torque, limit)):
        invalid("torque_body_n_m", f"must be within max_torque_body_n_m {limit!r} on each body axis", torque)


def build(devices, inputs) -> ActuatorGroup:
    """One extForceTorque per device. extForceTorque reads its command message in UpdateState, so
    it runs on the dynamics task before the spacecraft (§6) and the command written before Step(n)
    acts over [t_n-1, t_n)."""
    from Basilisk.architecture import messaging
    from Basilisk.simulation import extForceTorque

    effectors = []
    messages = {}
    for device in devices:
        effector = extForceTorque.ExtForceTorque()
        effector.ModelTag = f"extForceTorque[{device.device_id}]"
        message = messaging.CmdTorqueBodyMsg()
        message.write(_payload(IDLE["torque_body_n_m"]), 0)
        effector.cmdTorqueInMsg.subscribeTo(message)
        effectors.append(effector)
        messages[device.device_id] = message

    def write(commands, time_ns: int) -> None:
        for device_id, message in messages.items():
            message.write(_payload(commands[device_id]["torque_body_n_m"]), time_ns)

    return ActuatorGroup(
        device_ids=tuple(messages), modules=tuple(effectors), effectors=tuple(effectors), write=write,
        keep=tuple(messages.values()))


def _payload(torque):
    from Basilisk.architecture import messaging

    payload = messaging.CmdTorqueBodyMsgPayload()
    payload.torqueRequestBody = list(torque)
    return payload
