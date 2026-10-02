using System.Security.Cryptography;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StackReferenceNativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticReferenceSelectionKeepsSourceFilesAndOrderedMissingScores(bool bayer)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string missing = Path.Combine(directory, "missing.fits");
            string first = Path.Combine(directory, "first.fits");
            string equal = Path.Combine(directory, "equal.fits");
            StackingOptionsNativeTests.WriteStarField(first, 0, bayer, side: 512);
            File.Copy(first, equal);
            byte[] original = SHA256.HashData(File.ReadAllBytes(first));
            DateTime firstWrite = File.GetLastWriteTimeUtc(first);
            DateTime equalWrite = File.GetLastWriteTimeUtc(equal);
            string[] candidates = [missing, first, equal];

            StackReferenceSelection selection = await StackReferenceService.ChooseAsync(candidates);

            Assert.Equal(1, selection.SchemaVersion);
            Assert.Equal(1, selection.ReferenceIndex);
            Assert.Equal(first, selection.ReferencePath);
            Assert.Equal(candidates.Length, selection.Scores.Length);
            Assert.Null(selection.Scores[0]);
            Assert.NotNull(selection.Scores[1]);
            Assert.Equal(selection.Scores[1], selection.Scores[2]);
            Assert.True(selection.Scores[1]!.Stars > 0);
            Assert.True(selection.Scores[1]!.Score > 0);
            Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(first)));
            Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(equal)));
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(first));
            Assert.Equal(equalWrite, File.GetLastWriteTimeUtc(equal));
            Assert.False(File.Exists(missing));

            // The selected source then opens through the same published DLL
            // and contributes the reference ledger, rather than a rewritten proxy.
            await using ImageStackSession session = await ImageStackSession.OpenAsync(
                selection.ReferencePath, new ImageStackOptions(), new ImageStackCalibration());
            LiveStackNativeState state = await session.GetStateAsync();
            Assert.Equal(1, state.AcceptedFrames);
            Assert.True(LiveStackPath.Equals(first, Assert.Single(state.InputPaths)));
            Assert.False(string.IsNullOrWhiteSpace(state.CoreVersion));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutomaticReferenceCanChooseACleanerFrameAfterANoisyFirstCandidate()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string noisy = Path.Combine(directory, "noisy.fits");
            string clean = Path.Combine(directory, "clean.fits");
            StackingOptionsNativeTests.WriteStarField(noisy, 0, bayer: false, side: 512, noiseRange: 600);
            StackingOptionsNativeTests.WriteStarField(clean, 0, bayer: false, side: 512);
            string[] candidates = [noisy, clean];

            StackReferenceSelection selection = await StackReferenceService.ChooseAsync(candidates);

            Assert.Equal(1, selection.ReferenceIndex);
            Assert.Equal(clean, selection.ReferencePath);
            Assert.NotNull(selection.Scores[0]);
            Assert.NotNull(selection.Scores[1]);
            Assert.True(selection.Scores[1]!.Score > selection.Scores[0]!.Score);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutomaticReferenceReportsAllUnscoreableCandidatesClearly()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string unreadable = Path.Combine(directory, "unreadable.fits");
            File.WriteAllText(unreadable, "not a FITS image");
            string[] candidates = [Path.Combine(directory, "missing.fits"), unreadable];

            SeizaCoreException exception = await Assert.ThrowsAsync<SeizaCoreException>(() =>
                StackReferenceService.ChooseAsync(candidates));

            Assert.Contains("reference", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
