using System;
using System.Linq;
using System.Reflection;
using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class CameraProfileTests
    {
        [Test]
        public void Data6ProfileIsValid()
        {
            Assert.That(TestCameras.Imx708Profile().IsValid, Is.True);
        }

        // Only Brown-Conrady, in OpenCV's [k1, k2, p1, p2, k3] order. The same zero coefficients under
        // another model would mean a different projection (Kannala-Brandt is a fisheye).
        [TestCase("brown-conrady-5", true)]
        [TestCase("kannala-brandt", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void DistortionModelIsAClosedSet(string model, bool isValid)
        {
            Assert.That(With(new CameraDistortion(model, 0.0, 0.0, 0.0, 0.0, 0.0, "none")).IsValid, Is.EqualTo(isValid));
        }

        // No renderer applies distortion yet (UNITY-4), so a non-zero coefficient would record a
        // distortion the images never had.
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void NonZeroDistortionIsRejectedUntilARendererAppliesIt(int coefficient)
        {
            double[] c = new double[5];
            c[coefficient] = 1e-3;
            CameraDistortion distortion = new CameraDistortion("brown-conrady-5", c[0], c[1], c[2], c[3], c[4], "calibration");

            Assert.That(distortion.IsValid, Is.True);
            Assert.That(distortion.IsZero, Is.False);
            Assert.That(With(distortion).IsValid, Is.False);
        }

        [Test]
        public void NonFiniteDistortionIsInvalid()
        {
            Assert.That(new CameraDistortion("brown-conrady-5", double.NaN, 0.0, 0.0, 0.0, 0.0, "none").IsValid, Is.False);
        }

        // There is no noise model yet (DATA-6), so the only noise source is "none".
        [TestCase("none", true)]
        [TestCase("None", false)]
        [TestCase("dark-frames", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void NoiseSourceIsAClosedSet(string noiseSource, bool isValid)
        {
            Assert.That(Profile(noiseSource: noiseSource).IsValid, Is.EqualTo(isValid));
        }

        // The profile ID names a parameter set (intrinsics, distortion and noise) and is recorded.
        [TestCase("imx708-66deg-spec-v0", true)]
        [TestCase("IMX708", false)]
        [TestCase("imx708 v0", false)]
        [TestCase("", false)]
        public void ProfileIdIsALowercaseIdentifier(string profileId, bool isValid)
        {
            Assert.That(Profile(profileId: profileId).IsValid, Is.EqualTo(isValid));
        }

        // ProfileId versions the part's parameter set; exposure and extrinsics belong to each camera.
        // Every stored field is one or the other, so a new one (a noise model, say) must be sorted here
        // before two cameras can share an ID.
        [Test]
        public void EveryFieldIsVersionedByTheProfileIdOrBelongsToTheCamera()
        {
            string[] versioned = { "Intrinsics", "IntrinsicsSource", "IsCalibrated", "Distortion", "NoiseSource" };
            string[] perCamera = { "ExposureDurationSeconds", "Extrinsics" };
            // Every stored field, public or not; an auto-property's backing field is named <Name>k__BackingField.
            string[] properties = typeof(CameraProfile).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(field => field.Name.StartsWith("<", StringComparison.Ordinal) ? field.Name.Substring(1, field.Name.IndexOf('>') - 1) : field.Name)
                .Where(name => name != nameof(CameraProfile.ProfileId))
                .ToArray();
            Assert.That(properties, Is.EquivalentTo(versioned.Concat(perCamera)));

            CameraProfile profile = TestCameras.Imx708Profile();
            CameraIntrinsics otherLens = new CameraIntrinsics(4608, 2592, 3386.0, 3386.0, 2304.0, 1296.0);
            CameraExtrinsicsProvenance otherMount = new CameraExtrinsicsProvenance("cad", "geometric-centre", true);
            (string Property, CameraProfile Changed)[] changes =
            {
                ("Intrinsics", Profile(intrinsics: otherLens)),
                ("IntrinsicsSource", Profile(intrinsicsSource: "checkerboard")),
                ("IsCalibrated", Profile(isCalibrated: true)),
                ("Distortion", With(CameraDistortion.Zero("calibration"))),
                ("NoiseSource", Profile(noiseSource: "dark-frames")),
                ("ExposureDurationSeconds", Profile(exposureDurationSeconds: 0.002)),
                ("Extrinsics", Profile(extrinsics: otherMount))
            };
            Assert.That(changes.Select(change => change.Property), Is.EquivalentTo(properties));
            foreach ((string property, CameraProfile changed) in changes)
            {
                Assert.That(changed.IsSameParameterSetAs(profile), Is.EqualTo(perCamera.Contains(property)), property);
            }
        }

        [Test]
        public void EveryProvenanceFieldIsRequired()
        {
            Assert.That(Profile(intrinsicsSource: " ").IsValid, Is.False, "intrinsics source");
            Assert.That(With(CameraDistortion.Zero("")).IsValid, Is.False, "distortion source");
            Assert.That(Profile(extrinsics: new CameraExtrinsicsProvenance("", "assumed-com", false)).IsValid, Is.False, "extrinsics source");
            Assert.That(Profile(extrinsics: new CameraExtrinsicsProvenance("argus-2-config", null, false)).IsValid, Is.False, "extrinsics origin");
        }

        private static CameraProfile With(CameraDistortion distortion) => Profile(distortion: distortion);

        private static CameraProfile Profile(
            string profileId = "imx708-66deg-spec-v0",
            CameraIntrinsics? intrinsics = null,
            string intrinsicsSource = "spec-estimate",
            bool isCalibrated = false,
            CameraDistortion? distortion = null,
            string noiseSource = CameraProfile.NoNoise,
            double exposureDurationSeconds = 0.0005,
            CameraExtrinsicsProvenance? extrinsics = null) =>
            new CameraProfile(
                profileId,
                intrinsics ?? TestCameras.Imx708Intrinsics,
                intrinsicsSource,
                isCalibrated,
                distortion ?? CameraDistortion.Zero("none"),
                noiseSource,
                exposureDurationSeconds,
                extrinsics ?? new CameraExtrinsicsProvenance("argus-2-config", "assumed-com", false));
    }
}
