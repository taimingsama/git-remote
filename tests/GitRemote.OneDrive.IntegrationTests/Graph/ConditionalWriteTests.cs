using System.Net;
using Xunit;

namespace GitRemote.OneDrive.IntegrationTests.Graph;

public sealed class ConditionalWriteTests
{
    [OneDriveGraphFact]
    [Trait("Category", "OneDriveGraph")]
    public async Task ConditionalWrite_WithSameETag_AllowsOnlyOneWriter()
    {
        // Arrange
        const string originalContent = "base";
        const string firstReplacement = "value-a";
        const string secondReplacement = "value-b";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var file = new GraphTestFile(OneDriveGraphFactAttribute.AccessToken!);
        var sharedETag = await file.CreateAndReadETagAsync(originalContent, cancellationToken);

        // Act
        var competingWrites = await Task.WhenAll(
            file.ReplaceContentIfMatchAsync(firstReplacement, sharedETag, cancellationToken),
            file.ReplaceContentIfMatchAsync(secondReplacement, sharedETag, cancellationToken));

        // Assert
        var acceptedWrite = Assert.Single(competingWrites, write =>
            write.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created);
        Assert.Single(competingWrites, write => write.StatusCode == HttpStatusCode.PreconditionFailed);

        var storedContent = await file.ReadContentAsync(cancellationToken);
        Assert.Equal(acceptedWrite.Content, storedContent);
    }
}
