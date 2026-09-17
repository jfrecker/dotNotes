using DotNotes.Core.Config;

namespace DotNotes.Core.Tests.Config;

public class VaultPathValidatorTests
{
    [Fact]
    public void EnsureVaultRootExists_CreatesDirectory_WhenMissing()
    {
        var parent = Directory.CreateTempSubdirectory("dotnotes-tests-");
        try
        {
            var vaultPath = Path.Combine(parent.FullName, "vault-does-not-exist-yet");
            Assert.False(Directory.Exists(vaultPath));

            var result = VaultPathValidator.EnsureVaultRootExists(vaultPath);

            Assert.True(Directory.Exists(vaultPath));
            Assert.Equal(Path.GetFullPath(vaultPath), result);
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    public void EnsureVaultRootExists_IsIdempotent_WhenDirectoryAlreadyExists()
    {
        var vaultDirectory = Directory.CreateTempSubdirectory("dotnotes-tests-");
        try
        {
            var result = VaultPathValidator.EnsureVaultRootExists(vaultDirectory.FullName);

            Assert.True(Directory.Exists(vaultDirectory.FullName));
            Assert.Equal(Path.GetFullPath(vaultDirectory.FullName), result);
        }
        finally
        {
            vaultDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EnsureVaultRootExists_Throws_WhenPathIsNullOrWhitespace(string? rootPath)
    {
        Assert.Throws<ArgumentException>(() => VaultPathValidator.EnsureVaultRootExists(rootPath!));
    }
}
