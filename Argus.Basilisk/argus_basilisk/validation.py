"""ConfigureRun validation.

PLACEHOLDER: not implemented, not imported.

TODO(basilisk-team): README §3 "ConfigureRun" step 2. Validate every field before touching
Basilisk, repeating Core IsValid: orbit, spacecraft inertia and quaternion, kernel_set_id
pattern, sensor IDs and periods, profiles and the bsk 2.11.1 limits (IMU noise vs random walk,
walk bounds, light-sensor half-angle <= pi/2, magnetometer noise UNIMPLEMENTED). Return
INVALID_ARGUMENT, UNIMPLEMENTED or OUT_OF_RANGE (epoch outside kernel coverage or WMM2025)
before anything is built.
"""
