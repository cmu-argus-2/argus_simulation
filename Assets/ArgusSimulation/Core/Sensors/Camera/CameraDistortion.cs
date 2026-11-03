using System;

namespace Argus.Simulation.Core
{
    // A camera's lens distortion: the model and its coefficients, plus where they came from (DATA-6
    // #71). Only Brown-Conrady with five coefficients in OpenCV's order (k1, k2, p1, p2, k3) is
    // accepted. The same zero coefficients under another model would mean another projection.
    public readonly struct CameraDistortion
    {
        public const string BrownConrady5 = "brown-conrady-5";

        public CameraDistortion(string model, double k1, double k2, double p1, double p2, double k3, string source)
        {
            Model = model;
            K1 = k1;
            K2 = k2;
            P1 = p1;
            P2 = p2;
            K3 = k3;
            Source = source;
        }

        // No distortion, as DATA-6 v0 gives it: there is no calibration on hand.
        public static CameraDistortion Zero(string source) =>
            new CameraDistortion(BrownConrady5, 0.0, 0.0, 0.0, 0.0, 0.0, source);

        public string Model { get; }
        public double K1 { get; }
        public double K2 { get; }
        public double P1 { get; }
        public double P2 { get; }
        public double K3 { get; }

        // Where the coefficients came from, e.g. "none" or a calibration's name.
        public string Source { get; }

        public bool IsZero => K1 == 0.0 && K2 == 0.0 && P1 == 0.0 && P2 == 0.0 && K3 == 0.0;

        // The distortion models a camera may declare, one so far: IsValid and CameraKind's problem
        // check share this set, and CameraKind's message names its member.
        public static bool IsKnownModel(string model) => string.Equals(model, BrownConrady5, StringComparison.Ordinal);

        public bool IsValid =>
            IsKnownModel(Model) &&
            IsFinite(K1) && IsFinite(K2) && IsFinite(P1) && IsFinite(P2) && IsFinite(K3) &&
            !string.IsNullOrWhiteSpace(Source);

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
