using DotNotes.Core.Media;

namespace DotNotes.Core.Tests.Media;

/// <summary>
/// Unit tests for the shared content-type/extension allowlist used by
/// both <c>MediaEndpoints.PostUploadAsync</c> (validating an incoming
/// upload) and <c>MediaEndpoints.GetMediaAsync</c> (choosing a
/// Content-Type header for an existing file).
/// </summary>
public sealed class MediaContentTypesTests
{
    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("audio/mpeg")]
    [InlineData("audio/wav")]
    [InlineData("audio/ogg")]
    [InlineData("video/mp4")]
    [InlineData("video/webm")]
    [InlineData("application/pdf")]
    public void IsAcceptedContentType_AllowlistedType_ReturnsTrue(string contentType)
    {
        Assert.True(MediaContentTypes.IsAcceptedContentType(contentType));
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("text/html")]
    [InlineData("application/x-msdownload")]
    [InlineData(null)]
    [InlineData("")]
    public void IsAcceptedContentType_NotAllowlisted_ReturnsFalse(string? contentType)
    {
        Assert.False(MediaContentTypes.IsAcceptedContentType(contentType));
    }

    [Fact]
    public void GetCanonicalExtension_JpegPrefersDotJpgOverDotJpeg()
    {
        Assert.Equal(".jpg", MediaContentTypes.GetCanonicalExtension("image/jpeg"));
    }

    [Theory]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    public void ExtensionMatchesContentType_AcceptedSynonym_ReturnsTrue(string extension, string contentType)
    {
        Assert.True(MediaContentTypes.ExtensionMatchesContentType(extension, contentType));
    }

    [Fact]
    public void ExtensionMatchesContentType_MismatchedExtension_ReturnsFalse()
    {
        Assert.False(MediaContentTypes.ExtensionMatchesContentType(".exe", "image/png"));
    }

    [Fact]
    public void TryGetContentTypeForExtension_KnownExtension_ReturnsMappedContentType()
    {
        var found = MediaContentTypes.TryGetContentTypeForExtension(".pdf", out var contentType);

        Assert.True(found);
        Assert.Equal("application/pdf", contentType);
    }

    [Fact]
    public void TryGetContentTypeForExtension_UnknownExtension_FallsBackToOctetStream()
    {
        var found = MediaContentTypes.TryGetContentTypeForExtension(".exe", out var contentType);

        Assert.False(found);
        Assert.Equal("application/octet-stream", contentType);
    }
}
