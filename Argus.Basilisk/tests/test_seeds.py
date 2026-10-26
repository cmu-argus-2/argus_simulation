"""Per-module seeds (README §10); no Basilisk install."""

from argus_basilisk.seeds import ACCEL, CSS, GYRO, MAG, rng_seed

BASILISK_DEFAULT_SEED = 0x1BADCAD1


def test_seed_is_the_first_four_sha256_bytes_little_endian():
    # sha256(b"1:imu.main:gyro") begins 32 63 fd ef.
    assert rng_seed(1, "imu.main", GYRO) == 0xEFFD6332


def test_seed_is_stable_and_fits_rng_seed():
    assert rng_seed(7, "imu.main", ACCEL) == rng_seed(7, "imu.main", ACCEL)
    assert all(0 <= rng_seed(seed, "s", GYRO) < 2**32 for seed in range(100))


def test_each_input_changes_the_seed():
    seeds = {
        rng_seed(random_seed, sensor_id, stream)
        for random_seed in (0, 1)
        for sensor_id in ("imu.main", "imu.backup")
        for stream in (GYRO, ACCEL, MAG, CSS)
    }
    assert len(seeds) == 16
    assert BASILISK_DEFAULT_SEED not in seeds
