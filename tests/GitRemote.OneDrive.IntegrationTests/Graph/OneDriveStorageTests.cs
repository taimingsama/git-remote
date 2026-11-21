using System.Net;
using System.Text;
using Xunit;

namespace GitRemote.OneDrive.IntegrationTests.Graph;

[Trait("Category", "OneDriveGraph")]
public sealed class OneDriveStorageTests
{
    [OneDriveGraphFact]
    public async Task CreateIfAbsent_WithSameFileName_AllowsOnlyOneCreator()
    {
        // Arrange
        const string firstContent = "first creator";
        const string secondContent = "second creator";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var file = new GraphTestFile(OneDriveGraphFactAttribute.AccessToken!);
        await file.ResolveDriveAsync(cancellationToken);

        // Act
        var competingCreates = await Task.WhenAll(
            file.CreateIfAbsentAsync(firstContent, cancellationToken),
            file.CreateIfAbsentAsync(secondContent, cancellationToken));

        // Assert
        var acceptedCreate = Assert.Single(competingCreates, create =>
            create.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created);
        Assert.Single(competingCreates, create => create.StatusCode == HttpStatusCode.Conflict);
        var storedContent = await file.DownloadByPathFromAnotherClientAsync(cancellationToken);
        Assert.Equal(Encoding.UTF8.GetBytes(acceptedCreate.Content), storedContent);
    }

    [OneDriveGraphFact]
    public async Task ReadContent_WhenFileChangesAfterReadingETag_ExposesVersionChange()
    {
        // Arrange
        const string originalContent = "original content";
        const string replacementContent = "replacement content";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var file = new GraphTestFile(OneDriveGraphFactAttribute.AccessToken!);
        var etagBeforeDownload = await file.CreateAndReadETagAsync(originalContent, cancellationToken);

        // Act
        var update = await file.ReplaceContentIfMatchAsync(
            replacementContent, etagBeforeDownload, cancellationToken);
        var downloadedContent = await file.ReadContentAsync(cancellationToken);
        var etagAfterDownload = await file.ReadETagAsync(cancellationToken);

        // Assert
        Assert.Contains(update.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Created });
        Assert.Equal(replacementContent, downloadedContent);
        Assert.NotEqual(etagBeforeDownload, etagAfterDownload);
    }

    [OneDriveGraphFact]
    public async Task DownloadByPath_AfterUpload_ReturnsUploadedBytesToAnotherClient()
    {
        // Arrange
        byte[] uploadedContent = [0x00, 0x7f, 0x80, 0xff];
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var file = new GraphTestFile(OneDriveGraphFactAttribute.AccessToken!);
        await file.UploadAsync(uploadedContent, cancellationToken);

        // Act
        var downloadedContent = await file.DownloadByPathFromAnotherClientAsync(cancellationToken);

        // Assert
        Assert.Equal(uploadedContent, downloadedContent);
    }
}
