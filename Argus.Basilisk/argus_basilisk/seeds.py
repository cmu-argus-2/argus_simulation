"""Per-module random seeds.

PLACEHOLDER: not implemented, not imported.

TODO(basilisk-team): README §10. RNGSeed = first 4 bytes (little-endian) of
sha256(f"{random_seed}:{sensor_id}:{stream}"), with stream gyro, accel, mag or css. Never Python
hash() and never the shared default 0x1badcad1.
"""
