"""The light_sensor kind: a coarseSunSensor reporting irradiance, with Earth albedo (README §9,
BSK-6 #19)."""

import math

from .. import seeds
from ._common import NUMBER, Invalid, array, dcm, non_negative, number_value, positive

KIND = "light_sensor"

# coarseSunSensor scaleFactor = scale_factor x this, so a face square to the Sun at 1 AU reads
# W/m2.
SOLAR_IRRADIANCE_W_PER_M2 = 1361.0

# output = clip(scale (cos(theta) x shadow + albedo + bias) + noise, 0, saturation). README §9
# maps each key.
PARAMETERS = {
    "field_of_view_half_angle_rad": NUMBER,
    "scale_factor": NUMBER,
    "noise_std_w_per_m2": NUMBER,
    "bias_w_per_m2": NUMBER,
    "saturation_w_per_m2": NUMBER,
}

OUTPUTS = {
    # CSSRawDataMsgPayload.OutputData, with the service's noise and clip.
    "irradiance_w_per_m2": NUMBER,
}


def check(values, invalid: Invalid) -> None:
    half_angle = values.get("field_of_view_half_angle_rad")
    if half_angle is not None and not 0.0 < half_angle <= math.pi / 2.0:
        # coarseSunSensor and albedo gate on cos(angle) >= cos(fov) without clamping, so a wider
        # field adds negative irradiance from behind the aperture.
        invalid("field_of_view_half_angle_rad", "must be in (0, pi/2]", half_angle)
    positive(values, invalid, "scale_factor")
    # Noise > 0 is white noise added in the service (README §9, §10).
    non_negative(values, invalid, "noise_std_w_per_m2")
    positive(values, invalid, "saturation_w_per_m2")


def instrument(device) -> tuple:
    """(fov, nHat_B, r_B) of a light sensor, for coarseSunSensor and albedo addInstrumentConfig.

    The boresight is device +z, so nHat_B is the third row of C_SB = EP2C(device_to_body).
    """
    import numpy as np

    c_sb = dcm(device.mount.device_to_body)
    return (
        device.parameters["field_of_view_half_angle_rad"].number,
        np.array(c_sb[2, :]),
        array(device.mount.position_body_m),
    )


def build(device, random_seed: int, inputs) -> "LightSensor":
    albedo_msg = inputs.albedo_msgs.get(device.device_id)
    if inputs.sun_msg is None or inputs.eclipse_msg is None or albedo_msg is None:
        raise ValueError(f"light sensor {device.device_id!r} needs the Sun, eclipse and albedo outputs")
    return LightSensor(device, random_seed, inputs.spacecraft_state_msg, inputs.sun_msg, inputs.eclipse_msg, albedo_msg)


class LightSensor:
    """One Argus light sensor: a coarseSunSensor reporting irradiance in W/m2, with Earth albedo.

    OutputData = scale (cos(theta) x shadow + albedo + bias) + noise, clipped to [0, saturation],
    with scale = scale_factor x 1361 W/m2 and theta the angle between the boresight and the Sun.
    The module runs noise-free and unclipped, and output() adds the seeded noise from the "css"
    stream and clips (§9, §10, BSK-13 #116).
    """

    def __init__(self, device, random_seed: int, spacecraft_state_msg, sun_msg, eclipse_msg, albedo_msg):
        from Basilisk.simulation import coarseSunSensor

        parameters = device.parameters
        self.device_id = device.device_id
        self.sample_period_ns = device.sample_period_ns
        self.rng_seed = seeds.rng_seed(random_seed, self.device_id, seeds.CSS)
        self._noise_std = parameters["noise_std_w_per_m2"].number
        self._saturation = parameters["saturation_w_per_m2"].number
        field_of_view, boresight_body, position_body = instrument(device)
        scale = parameters["scale_factor"].number * SOLAR_IRRADIANCE_W_PER_M2

        self.module = coarseSunSensor.CoarseSunSensor()
        self.module.ModelTag = f"css[{self.device_id}]"
        self.module.stateInMsg.subscribeTo(spacecraft_state_msg)
        self.module.sunInMsg.subscribeTo(sun_msg)
        self.module.sunEclipseInMsg.subscribeTo(eclipse_msg)
        self.module.albedoInMsg.subscribeTo(albedo_msg)
        self.module.fov = field_of_view
        # nHat_B is set directly; setUnitDirectionVectorWithPerturbation is never used.
        self.module.nHat_B = boresight_body
        self.module.r_B = position_body
        self.module.scaleFactor = scale
        # The bias is in units of the unscaled output, so the W/m2 value is divided by the scale.
        # Noise and clipping are applied in output(), so a sample depends only on its index (§10).
        self.module.senNoiseStd = 0.0
        self.module.senBias = parameters["bias_w_per_m2"].number / scale
        self.module.maxOutput = math.inf
        self.module.minOutput = -math.inf
        self.module.kellyFactor = 0.0
        self._reader = self.module.cssDataOutMsg.addSubscriber()

    @property
    def modules(self) -> tuple:
        return (self.module,)

    @property
    def readers(self) -> dict:
        """For freshness checks only: cssDataOutMsg holds the noise-free, unclipped irradiance, and
        the sensor's output is output()."""
        return {f"{self.module.ModelTag}.cssDataOutMsg": self._reader}

    @property
    def ready(self) -> bool:
        """Always: the sensor measures on its first firing, from the Sun, eclipse and albedo at t_n."""
        return True

    def output(self) -> dict:
        irradiance = self._reader().OutputData
        if self._noise_std > 0.0:
            k = seeds.sample_index(self._reader.timeWritten(), self.sample_period_ns)
            irradiance += self._noise_std * float(seeds.sample_normals(self.rng_seed, seeds.CSS, k, size=1)[0])
        return {"irradiance_w_per_m2": number_value(min(max(irradiance, 0.0), self._saturation))}
