using System;

namespace Argus.Simulation.Core
{
    // Predicts where the spacecraft will be a short time ahead, for streaming ground imagery
    // before a camera arrives. It rotates the position about the instantaneous orbit normal and
    // then applies Earth rotation, so it is exact for a circular orbit and a close approximation
    // otherwise. It depends only on the current state, never on the state source, so it cannot
    // disturb a stateful backend.
    public static class OrbitLookahead
    {
        public static Vector3d PredictPositionEcef(SpacecraftState state, double leadSeconds)
        {
            Vector3d position = state.PositionEcefMeters;
            if (leadSeconds == 0.0)
            {
                return position;
            }

            // Inertial velocity expressed in the current ECEF axes: v_ecef + omega x r.
            double earthRate = CircularOrbitModel.EarthRotationRadiansPerSecond;
            Vector3d inertialVelocity = state.VelocityEcefMetersPerSecond + new Vector3d(
                -earthRate * position.Y,
                earthRate * position.X,
                0.0);

            Vector3d angularMomentum = Vector3d.Cross(position, inertialVelocity);
            double momentumMagnitude = angularMomentum.Magnitude;
            double radiusSquared = Vector3d.Dot(position, position);
            if (momentumMagnitude <= 0.0 || radiusSquared <= 0.0)
            {
                return position;
            }

            Vector3d axis = angularMomentum / momentumMagnitude;
            double orbitAngle = momentumMagnitude / radiusSquared * leadSeconds;
            double cosine = Math.Cos(orbitAngle);
            double sine = Math.Sin(orbitAngle);
            Vector3d inertialFuture =
                position * cosine +
                Vector3d.Cross(axis, position) * sine +
                axis * (Vector3d.Dot(axis, position) * (1.0 - cosine));

            // The ECEF frame keeps rotating during the lead time.
            double earthAngle = -earthRate * leadSeconds;
            double earthCosine = Math.Cos(earthAngle);
            double earthSine = Math.Sin(earthAngle);
            return new Vector3d(
                earthCosine * inertialFuture.X - earthSine * inertialFuture.Y,
                earthSine * inertialFuture.X + earthCosine * inertialFuture.Y,
                inertialFuture.Z);
        }
    }
}
