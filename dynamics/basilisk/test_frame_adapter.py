import unittest

import numpy as np
from Basilisk.utilities import RigidBodyKinematics as rbk

from frame_adapter import EARTH_RATE, convert_records, translate


def matrix(q):
    # Independent reference: Basilisk EP2C is passive; output is active xyzw.
    return rbk.EP2C(np.r_[q[3], q[:3]]).T


class FrameAdapterTests(unittest.TestCase):
    def test_identity_epoch(self):
        r, v, q = translate(0, [7e6, 0, 0], [0, 7500, 0], [0, 0, 0])
        np.testing.assert_allclose(r, [7e6, 0, 0])
        np.testing.assert_allclose(v, [0, 7500 - EARTH_RATE * 7e6, 0])
        np.testing.assert_allclose(q, [0, 0, 0, 1])

    def test_quarter_earth_turn(self):
        r, _, q = translate(np.pi / (2 * EARTH_RATE), [1, 0, 0], [0, 0, 0], [0, 0, 0])
        np.testing.assert_allclose(r, [0, -1, 0], atol=1e-14)
        np.testing.assert_allclose(matrix(q) @ [1, 0, 0], r, atol=1e-14)

    def test_velocity_is_position_derivative(self):
        t, dt = 1234., .01
        r0, v0 = np.array([7e6, 2e6, 1e6]), np.array([200., 7400., -500.])
        before = translate(t - dt, r0 - v0 * dt, v0, [0, 0, 0])[0]
        after = translate(t + dt, r0 + v0 * dt, v0, [0, 0, 0])[0]
        velocity = translate(t, r0, v0, [0, 0, 0])[1]
        np.testing.assert_allclose((after - before) / (2 * dt), velocity, atol=1e-6, rtol=0)

    def test_corotating_object_has_zero_fixed_velocity(self):
        r = np.array([7e6, 1e6, 2e6])
        v = np.cross([0, 0, EARTH_RATE], r)
        np.testing.assert_allclose(translate(600, r, v, [0, 0, 0])[1], 0, atol=1e-12)

    def test_attitude_matches_basilisk_mrp_convention(self):
        sigma = np.array([.2, -.3, .1])
        t = 4567.
        angle = EARTH_RATE * t
        c, s = np.cos(angle), np.sin(angle)
        rotation = np.array([[c, s, 0], [-s, c, 0], [0, 0, 1]])
        q = translate(t, [1, 2, 3], [4, 5, 6], sigma)[2]
        np.testing.assert_allclose(matrix(q), rotation @ rbk.MRP2C(sigma).T, atol=1e-14)
        self.assertAlmostEqual(np.linalg.norm(q), 1.)
        shadow = -sigma / np.dot(sigma, sigma)
        q_shadow = translate(t, [1, 2, 3], [4, 5, 6], shadow)[2]
        np.testing.assert_allclose(matrix(q_shadow), matrix(q), atol=1e-14)

    def test_stream_keeps_gyro_and_clock_separate_from_truth(self):
        record = dict(schema='basilisk-eci-gyro-demo-v1', time_s=.05,
                      sequence=1, timestamp_utc='2026-09-27T00:00:00.050000Z',
                      position_N_m=[7e6, 0, 0], velocity_N_m_s=[0, 7500, 0],
                      sigma_BN=[0, 0, 0], omega_BN_B_rad_s=[.01, 0, 0],
                      noisy_gyro_body_rad_s=[.012, .001, 0])
        result = list(convert_records([record]))[0]
        self.assertEqual(result['timestamp_utc'], record['timestamp_utc'])
        self.assertEqual(result['measurements']['gyro_body_rad_s'], [.012, .001, 0])
        self.assertEqual(result['truth']['angular_velocity_body_rad_s'], [.01, 0, 0])
        with self.assertRaises(ValueError):
            list(convert_records([record, record]))

    def test_rejects_invalid_inputs(self):
        for time in [-1, float('nan'), float('inf')]:
            with self.assertRaises(ValueError):
                translate(time, [1, 2, 3], [0, 0, 0], [0, 0, 0])
        with self.assertRaises(ValueError):
            translate(0, [1, 2], [0, 0, 0], [0, 0, 0])


if __name__ == '__main__':
    unittest.main()
