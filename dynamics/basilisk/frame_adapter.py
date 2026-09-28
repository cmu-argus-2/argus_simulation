"""Offline frame translation, NOT a Unity transport or astronomical ECI/ITRF model.

Synthetic Earth-fixed axes equal scenario N at t=0, then rotate at the same
constant rate as Argus CircularOrbitModel. UTC is only a label. Replace this
profile with satcoords once its inertial-frame/time contract is agreed.
"""
import argparse
import json
from pathlib import Path

import numpy as np

EARTH_RATE = 7.2921150e-5
PROFILE = 'synthetic-earth-fixed-aligned-at-t0-v1'


def vector3(value):
    result = np.asarray(value, dtype=float)
    if result.shape != (3,) or not np.isfinite(result).all():
        raise ValueError('Expected a finite 3-vector')
    return result


def multiply_quaternions(a, b):
    """Hamilton product, active rotations, xyzw order."""
    av, bv = a[:3], b[:3]
    return np.r_[a[3] * bv + b[3] * av + np.cross(av, bv),
                 a[3] * b[3] - np.dot(av, bv)]


def translate(time_s, position_n, velocity_n, sigma_bn):
    """Return synthetic fixed-frame r, dr/dt and active body-to-fixed xyzw."""
    if not np.isfinite(time_s) or time_s < 0:
        raise ValueError('Expected finite nonnegative elapsed seconds')
    r, v, sigma = map(vector3, (position_n, velocity_n, sigma_bn))
    angle = EARTH_RATE * time_s
    c, s = np.cos(angle), np.sin(angle)
    c_en = np.array([[c, s, 0.], [-s, c, 0.], [0., 0., 1.]])
    r_e = c_en @ r
    v_e = c_en @ (v - np.cross([0., 0., EARTH_RATE], r))
    # Basilisk sigma_BN is passive N->B. Its Euler parameters represent the
    # corresponding active B->N rotation when interpreted as Hamilton xyzw.
    squared = np.dot(sigma, sigma)
    q_nb = np.r_[2 * sigma, 1 - squared] / (1 + squared)
    q_en = np.array([0., 0., -np.sin(angle / 2), np.cos(angle / 2)])
    q_eb = multiply_quaternions(q_en, q_nb)
    q_eb /= np.linalg.norm(q_eb)
    return r_e, v_e, q_eb


def convert_records(records):
    previous_time, previous_q = -1., None
    for record in records:
        if record['schema'] != 'basilisk-eci-gyro-demo-v1':
            raise ValueError('Unsupported source schema')
        time_s = record['time_s']
        if time_s <= previous_time:
            raise ValueError('Samples must have strictly increasing times')
        r, v, q = translate(time_s, record['position_N_m'],
                            record['velocity_N_m_s'], record['sigma_BN'])
        if previous_q is not None and np.dot(previous_q, q) < 0:
            q = -q  # same rotation, continuous quaternion representation
        previous_time, previous_q = time_s, q
        yield {
            'schema': 'basilisk-frame-replay-demo-v1', 'frame_profile': PROFILE,
            'sequence': record['sequence'], 'simulation_time_s': time_s,
            'timestamp_utc': record['timestamp_utc'],
            'truth': {
                'position_fixed_m': r.tolist(), 'velocity_fixed_m_s': v.tolist(),
                'body_to_fixed_xyzw': q.tolist(),
                'angular_velocity_body_rad_s': vector3(record['omega_BN_B_rad_s']).tolist(),
            },
            'measurements': {
                'gyro_body_rad_s': vector3(record['noisy_gyro_body_rad_s']).tolist(),
            },
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    output = args.output or args.input.with_name('synthetic_fixed_samples.jsonl')
    if output.resolve() == args.input.resolve():
        parser.error('Output must not replace the input')
    with args.input.open() as stream:
        converted = list(convert_records(json.loads(line) for line in stream if line.strip()))
    if not converted:
        parser.error('Input contains no samples')
    # Exclusive creation avoids silently replacing an earlier experiment.
    with output.open('x') as stream:
        for record in converted:
            stream.write(json.dumps(record, allow_nan=False) + '\n')
    print(f'Converted {len(converted)} samples: {output}')
    print(f'Profile: {PROFILE}; NOT astronomical ECEF or a connected Unity stream.')


if __name__ == '__main__':
    main()
