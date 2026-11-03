"""Per-module random seeds (README §10).

Every noisy module gets its own seed, derived from the run seed, the Argus sensor ID and the
noise stream, so changing one sensor or stream never changes another module's noise. Never
Python hash(), which is salted per process, and never the shared module default 0x1badcad1.

Streams bsk 2.11.1 cannot seed or shape are drawn by the service, one sample at a time
(sample_normals), keyed by sample_index. numpy is imported only to draw them, so the device
registry, which imports this module, stays importable without the basilisk extra.
"""

import hashlib

__all__ = ["ACCEL", "CSS", "GYRO", "MAG", "rng_seed", "sample_index", "sample_normals"]

# Noise streams.
GYRO = "gyro"
ACCEL = "accel"
MAG = "mag"
CSS = "css"


def rng_seed(random_seed: int, sensor_id: str, stream: str) -> int:
    """RNGSeed: the first 4 bytes, little-endian, of sha256(f"{random_seed}:{sensor_id}:{stream}")."""
    digest = hashlib.sha256(f"{random_seed}:{sensor_id}:{stream}".encode()).digest()
    return int.from_bytes(digest[:4], "little")


def sample_index(sample_time_ns: int, sample_period_ns: int) -> int:
    """k of a sample written at k x sample_period_ns; an off-grid time raises ValueError, so two
    samples never share k and therefore their noise."""
    k, remainder = divmod(sample_time_ns, sample_period_ns)
    if remainder:
        raise ValueError(f"sample time {sample_time_ns} ns is not a multiple of the period {sample_period_ns} ns")
    return k


def sample_normals(rng_seed: int, stream: str, k: int, size: int = 3):
    """size standard normals for sample k of a stream the service draws itself, as a NumPy array.

    PCG64 seeded with SeedSequence([tag, rng_seed, k]), tag being the first 4 bytes,
    little-endian, of sha256(f"sample:{stream}"). A sample depends only on its seed, stream and
    index. The tag keeps every draw apart from a generator seeded with rng_seed alone, which
    SeedSequence's zero padding would otherwise make equal to sample 0.
    """
    import numpy as np

    tag = int.from_bytes(hashlib.sha256(f"sample:{stream}".encode()).digest()[:4], "little")
    return np.random.default_rng([tag, rng_seed, k]).standard_normal(size)
