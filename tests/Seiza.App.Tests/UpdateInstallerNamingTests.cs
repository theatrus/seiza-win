using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class UpdateInstallerNamingTests
{
    [Fact]
    public void FromDownloadLinkPreservesGitHubReleaseAssetName()
    {
        const string downloadLink =
            "https://github.com/theatrus/seiza-win/releases/download/v0.5.1/seiza-0.5.1-windows-x86_64.msi";

        string fileName = UpdateInstallerNaming.FromDownloadLink(downloadLink);

        Assert.Equal("seiza-0.5.1-windows-x86_64.msi", fileName);
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/asset-id")]
    [InlineData("https://example.com/seiza.zip")]
    [InlineData("http://example.com/seiza.msi")]
    [InlineData("")]
    public void FromDownloadLinkRejectsUnsafeOrExtensionlessLinks(string downloadLink)
    {
        Assert.Throws<InvalidDataException>(() =>
            UpdateInstallerNaming.FromDownloadLink(downloadLink));
    }
}
