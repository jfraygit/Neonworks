using System.IO;
using Nightshare.Core;
using Xunit;

namespace Nightshare.Tests
{
    /// <summary>
    /// These exist because of a real bug. The plugin computed the build id from file sizes
    /// while the fake peer still claimed a hardcoded Steam build id, so the two refused to
    /// connect over an identical install. One shared implementation, pinned here.
    /// </summary>
    public class GameFingerprintTests
    {
        /// <summary>Builds a directory that looks enough like a game install to fingerprint.</summary>
        private static string MakeFakeInstall(int assemblyBytes, int metadataBytes)
        {
            var root = Path.Combine(Path.GetTempPath(), "nightshare-test-" + Path.GetRandomFileName());
            var metaDir = Path.Combine(root, "Nivalis Nights_Data", "il2cpp_data", "Metadata");
            Directory.CreateDirectory(metaDir);

            File.WriteAllBytes(Path.Combine(root, "GameAssembly.dll"), new byte[assemblyBytes]);
            File.WriteAllBytes(Path.Combine(metaDir, "global-metadata.dat"), new byte[metadataBytes]);

            return root;
        }

        [Fact]
        public void FingerprintsAnInstallFromFileSizes()
        {
            var root = MakeFakeInstall(1234, 567);
            try
            {
                Assert.Equal("1234-567", GameFingerprint.Compute(root));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void TwoIdenticalInstallsAgree()
        {
            var a = MakeFakeInstall(72366592, 12652528);
            var b = MakeFakeInstall(72366592, 12652528);
            try
            {
                Assert.Equal(GameFingerprint.Compute(a), GameFingerprint.Compute(b));
            }
            finally { Directory.Delete(a, true); Directory.Delete(b, true); }
        }

        /// <summary>A patch changes at least one of the two files, so the id must change.</summary>
        [Fact]
        public void APatchedInstallFingerprintsDifferently()
        {
            var before = MakeFakeInstall(72366592, 12652528);
            var after = MakeFakeInstall(72366600, 12652528);
            try
            {
                Assert.NotEqual(GameFingerprint.Compute(before), GameFingerprint.Compute(after));
            }
            finally { Directory.Delete(before, true); Directory.Delete(after, true); }
        }

        [Fact]
        public void AMissingInstallIsUnknownRatherThanAThrow()
        {
            Assert.Equal(GameFingerprint.Unknown,
                GameFingerprint.Compute(@"Z:\definitely\not\here"));

            Assert.Equal(GameFingerprint.Unknown, GameFingerprint.Compute(null));
            Assert.Equal(GameFingerprint.Unknown, GameFingerprint.Compute(""));
        }

        [Fact]
        public void AHalfInstallIsUnknown()
        {
            var root = Path.Combine(Path.GetTempPath(), "nightshare-test-" + Path.GetRandomFileName());
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "GameAssembly.dll"), new byte[10]);
            try
            {
                // GameAssembly.dll present but no metadata.
                Assert.Equal(GameFingerprint.Unknown, GameFingerprint.Compute(root));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("unknown", true)]
        [InlineData("72366592-12652528", false)]
        public void IsUnknownRecognisesTheSentinel(string value, bool expected)
        {
            Assert.Equal(expected, GameFingerprint.IsUnknown(value));
        }

        /// <summary>
        /// The bug in one line: a peer must never claim a Steam build id where a
        /// fingerprint is expected. The two formats are not interchangeable.
        /// </summary>
        [Fact]
        public void ASteamBuildIdIsNotAFingerprint()
        {
            var root = MakeFakeInstall(72366592, 12652528);
            try
            {
                Assert.NotEqual("25603526", GameFingerprint.Compute(root));
                Assert.Contains("-", GameFingerprint.Compute(root));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
