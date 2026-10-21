"""Per-module random seeds (README §10).

Every noisy module gets its own seed, derived from the run seed, the Argus sensor ID and the
noise stream, so changing one sensor or stream never changes another module's noise. Never
Python hash(), which is salted per process, and never the shared module default 0x1badcad1.
"""

import hashlib

__all__ = ["ACCEL", "CSS", "GYRO", "MAG", "rng_seed"]

# Noise streams.
GYRO = "gyro"
ACCEL = "accel"
MAG = "mag"
CSS = "css"


def rng_seed(random_seed: int, sensor_id: str, stream: str) -> int:
    """RNGSeed: the first 4 bytes, little-endian, of sha256(f"{random_seed}:{sensor_id}:{stream}")."""
    digest = hashlib.sha256(f"{random_seed}:{sensor_id}:{stream}".encode()).digest()
    return int.from_bytes(digest[:4], "little")
