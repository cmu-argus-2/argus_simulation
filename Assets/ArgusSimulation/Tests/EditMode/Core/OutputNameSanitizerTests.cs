using Argus.Simulation.Core;
using NUnit.Framework;

namespace Argus.Simulation.Tests
{
    public sealed class OutputNameSanitizerTests
    {
        [Test]
        public void Sanitize_ReplacesTraversalAndPathSeparators()
        {
            string result = OutputNameSanitizer.Sanitize("../mission\\map", "map");

            Assert.That(result, Is.EqualTo("___mission_map"));
            Assert.That(result, Does.Not.Contain(".."));
            Assert.That(result, Does.Not.Contain("/"));
            Assert.That(result, Does.Not.Contain("\\"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Sanitize_UsesFallbackForMissingName(string value)
        {
            Assert.That(OutputNameSanitizer.Sanitize(value, "map"), Is.EqualTo("map"));
        }

        [Test]
        public void Sanitize_PreservesPortableNameCharacters()
        {
            Assert.That(
                OutputNameSanitizer.Sanitize("map_2026-09", "fallback"),
                Is.EqualTo("map_2026-09"));
        }
    }
}
