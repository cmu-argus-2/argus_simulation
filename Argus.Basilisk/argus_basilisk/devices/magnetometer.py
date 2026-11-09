"""The magnetometer kind: a TAM reading the MagneticFieldWMM field (README §9, BSK-5 #18)."""

import math

from .. import seeds
from ._common import NUMBER, VECTOR3, Invalid, array, dcm, non_negative, positive, vector3_value

KIND = "magnetometer"

# output = clip(scale (B + noise + bias), -range, +range). README §9 maps each key.
PARAMETERS = {
    "noise_std_tesla": NUMBER,
    "constant_bias_sensor_tesla": VECTOR3,
    "scale_factor": NUMBER,
    "range_tesla": NUMBER,
}

OUTPUTS = {
    # TAMSensorMsgPayload.tam_S, with the service's noise and clip.
    "magnetic_field_sensor_tesla": VECTOR3,
}


def check(values, invalid: Invalid) -> None:
    # Noise > 0 is seeded in the service, since bsk 2.11.1 ignores the module's RNGSeed (README §9).
    non_negative(values, invalid, "noise_std_tesla")
    positive(values, invalid, "scale_factor")
    positive(values, invalid, "range_tesla")


def build(device, random_seed: int, inputs) -> "Magnetometer":
    if inputs.magnetic_field_msg is None:
        raise ValueError(f"magnetometer {device.device_id!r} needs the MagneticFieldWMM output")
    return Magnetometer(device, random_seed, inputs.spacecraft_state_msg, inputs.magnetic_field_msg)


class Magnetometer:
    """One Argus magnetometer (TAM) reading the MagneticFieldWMM field at the spacecraft.

    tam_S = scale (C_SB C_BN B_N + noise + bias), clipped to +-range. bsk 2.11.1 seeds the
    module's noise generator once in its constructor and ignores RNGSeed, so the module runs
    noise-free and unclipped, and output() adds the seeded noise from the "mag" stream and clips
    (§9, BSK-9 #22).
    """

    def __init__(self, device, random_seed: int, spacecraft_state_msg, magnetic_field_msg):
        import numpy as np
        from Basilisk.simulation import magnetometer

        parameters = device.parameters
        self.device_id = device.device_id
        self.sample_period_ns = device.sample_period_ns
        self.rng_seed = seeds.rng_seed(random_seed, self.device_id, seeds.MAG)
        self._noise_std = parameters["noise_std_tesla"].number
        self._scale = parameters["scale_factor"].number
        self._range = parameters["range_tesla"].number

        self.module = magnetometer.Magnetometer()
        self.module.ModelTag = f"magnetometer[{self.device_id}]"
        self.module.stateInMsg.subscribeTo(spacecraft_state_msg)
        self.module.magInMsg.subscribeTo(magnetic_field_msg)
        self.module.dcm_SB = dcm(device.mount.device_to_body)
        # Every error term is set explicitly: the module defaults are a random walk (A = I) and
        # noise -1. Noise and clipping are applied in output().
        self.module.senNoiseStd = np.zeros(3)
        self.module.setAMatrix(np.zeros((3, 3)))
        self.module.walkBounds = np.zeros(3)
        self.module.senBias = array(parameters["constant_bias_sensor_tesla"].vector3)
        self.module.scaleFactor = self._scale
        self.module.maxOutput = math.inf
        self.module.minOutput = -math.inf
        self._reader = self.module.tamDataOutMsg.addSubscriber()

    @property
    def modules(self) -> tuple:
        return (self.module,)

    @property
    def readers(self) -> dict:
        """For freshness checks only: tamDataOutMsg holds the noise-free, unclipped field, and the
        sensor's output is output()."""
        return {f"{self.module.ModelTag}.tamDataOutMsg": self._reader}

    @property
    def ready(self) -> bool:
        """Always: the magnetometer measures on its first firing, from the field written at t_n."""
        return True

    def output(self) -> dict:
        import numpy as np

        field = np.array(self._reader().tam_S)
        if self._noise_std > 0.0:
            k = seeds.sample_index(self._reader.timeWritten(), self.sample_period_ns)
            field = field + self._scale * self._noise_std * seeds.sample_normals(self.rng_seed, seeds.MAG, k)
        return {"magnetic_field_sensor_tesla": vector3_value(np.clip(field, -self._range, self._range))}
