namespace Argus.Simulation.Core
{
    // Where a camera's mount (SensorDefinition.Mount) came from (DATA-6 #71). It describes this
    // camera's placement, not the part, so CameraProfile.ProfileId does not version it.
    public readonly struct CameraExtrinsicsProvenance
    {
        public CameraExtrinsicsProvenance(string source, string origin, bool isOrientationVerified)
        {
            Source = source;
            Origin = origin;
            IsOrientationVerified = isOrientationVerified;
        }

        // e.g. "argus-2-config": the R and t matrices in the Argus-2 flight and GNC configs.
        public string Source { get; }

        // The body point the mount position is measured from, e.g. "assumed-com".
        public string Origin { get; }

        // False until someone checks on hardware that no camera is mounted flipped.
        public bool IsOrientationVerified { get; }

        public bool IsValid => !string.IsNullOrWhiteSpace(Source) && !string.IsNullOrWhiteSpace(Origin);
    }
}
