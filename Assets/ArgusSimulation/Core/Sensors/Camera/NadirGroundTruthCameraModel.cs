using System;

namespace Argus.Simulation.Core
{
    // The north-up nadir ground-truth camera (D5, CORE-8): a reference view straight down from the
    // spacecraft position that ignores attitude. It is ground truth, so it goes to the recorder but
    // never to a controller (its kind may not be controller-visible, D7). NadirGroundTruthCameraKind
    // builds it from a nadir_ground_truth_camera device configuration.
    public sealed class NadirGroundTruthCameraModel : CameraModel
    {
        // WGS84, the ellipsoid Cesium and the Unity globe use.
        private const double EquatorialRadiusMeters = 6_378_137.0;
        private const double Flattening = 1.0 / 298.257223563;
        private const double EccentricitySquared = Flattening * (2.0 - Flattening);

        // Fixed-point iterations for geodetic latitude; 6 converge to below 1e-15 rad from the
        // surface to well past low Earth orbit.
        private const int LatitudeIterations = 6;

        // Below this distance from the polar axis, north is undefined; see ComputePose.
        private const double PolarAxisToleranceMeters = 1e-3;

        // As for ArducamImx708CameraModel: never gives up on a render, which is safe only while no
        // caller supplies a renderer, and a caller cannot wait for renders yet (CameraModel README).
        // TODO(G3): take the limit, or a way to wait, from the first caller that supplies a renderer (UNITY-4).
        private const double RenderLatencyLimitSeconds = double.PositiveInfinity;

        // NadirGroundTruthCameraKind.Profile checks the configuration once, before the base
        // constructor, so a wrong one names the cause.
        public NadirGroundTruthCameraModel(DeviceConfiguration configuration, IImageRenderer renderer)
            : this(configuration.Definition, NadirGroundTruthCameraKind.Instance.Profile(configuration), renderer)
        {
        }

        private NadirGroundTruthCameraModel(SensorDefinition definition, CameraProfile profile, IImageRenderer renderer)
            : base(definition, profile.Intrinsics, profile.ExposureDurationSeconds, RenderLatencyLimitSeconds, renderer)
        {
        }

        // The camera sits at the spacecraft position (the mount is ignored) and looks along geodetic
        // down: the line through the spacecraft perpendicular to the WGS84 ellipsoid, so the image
        // centre is the geodetic point directly beneath it. (Cesium's local East-North-Up frame, used
        // by the Unity ground-truth camera in CubeSatCameraRig, takes the ellipsoid gradient at the
        // spacecraft itself instead; at 500 km the two differ by about 0.01 degrees.) In the camera frame
        // (x right, y down, z out of the lens; DATA-6, #71): z is down, -y is north, so north is up in
        // the image, and x is east. Exactly on the polar axis north is undefined; there east is taken
        // as Earth-fixed +y, as Cesium does, and north completes the frame.
        protected override void ComputePose(
            SpacecraftState spacecraft,
            out Vector3d cameraPositionEcefMeters,
            out Quaterniond cameraToEcef)
        {
            Vector3d position = spacecraft.PositionEcefMeters;
            cameraPositionEcefMeters = position;

            Vector3d up = GeodeticUp(position);
            Vector3d east = Math.Sqrt(position.X * position.X + position.Y * position.Y) < PolarAxisToleranceMeters
                ? new Vector3d(0.0, 1.0, 0.0)
                : Vector3d.Cross(new Vector3d(0.0, 0.0, 1.0), up).Normalized();
            Vector3d north = Vector3d.Cross(up, east);

            Vector3d down = new Vector3d(-up.X, -up.Y, -up.Z);
            Vector3d south = new Vector3d(-north.X, -north.Y, -north.Z);
            cameraToEcef = Quaterniond.FromBasis(east, south, down).Normalized();
        }

        // Geodetic up at the point: (cos phi cos lambda, cos phi sin lambda, sin phi), with phi the
        // geodetic latitude, found by iterating tan phi = (z + e^2 N(phi) sin phi) / p, which needs no
        // height and stays defined on the polar axis.
        internal static Vector3d GeodeticUp(Vector3d positionEcefMeters)
        {
            double x = positionEcefMeters.X;
            double y = positionEcefMeters.Y;
            double z = positionEcefMeters.Z;
            double p = Math.Sqrt(x * x + y * y);
            double latitude = Math.Atan2(z, p * (1.0 - EccentricitySquared));
            for (int i = 0; i < LatitudeIterations; i++)
            {
                double sine = Math.Sin(latitude);
                double primeVerticalRadius = EquatorialRadiusMeters / Math.Sqrt(1.0 - EccentricitySquared * sine * sine);
                latitude = Math.Atan2(z + EccentricitySquared * primeVerticalRadius * sine, p);
            }

            double longitude = Math.Atan2(y, x);
            return new Vector3d(
                Math.Cos(latitude) * Math.Cos(longitude),
                Math.Cos(latitude) * Math.Sin(longitude),
                Math.Sin(latitude));
        }
    }
}
