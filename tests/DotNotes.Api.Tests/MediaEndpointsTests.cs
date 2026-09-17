using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNotes.Api.Tests;

/// <summary>
/// Integration tests for docs/04-API-SPEC.md's Media section
/// (<c>POST /api/upload</c>) plus the top-level <c>GET /media/{**path}</c>
/// serving endpoint, exercised end-to-end through a real ASP.NET Core
/// test host (see <see cref="NotesApiFactory"/>).
/// </summary>
public sealed class MediaEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _vaultRootPath;
    private readonly NotesApiFactory _factory;
    private readonly HttpClient _client;

    public MediaEndpointsTests()
    {
        _vaultRootPath = Directory.CreateTempSubdirectory("dotnotes-media-api-tests-").FullName;
        _factory = new NotesApiFactory(_vaultRootPath);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Vault__RootPath", null);

        if (Directory.Exists(_vaultRootPath))
        {
            Directory.Delete(_vaultRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task PostUpload_PngFile_SavesUnderMediaAndReturnsRelativePath()
    {
        using var content = new MultipartFormDataContent();
        var fileBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 }; // fake PNG-ish bytes
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", "photo.png");

        var response = await _client.PostAsync("/api/upload", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<UploadResponseDto>(ResponseJsonOptions);
        Assert.NotNull(result);
        Assert.StartsWith("_media/", result!.RelativePath);
        Assert.EndsWith(".png", result.RelativePath);

        var fullPath = Path.Combine(_vaultRootPath, "_media", Path.GetFileName(result.RelativePath));
        Assert.True(File.Exists(fullPath));
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(fullPath));
    }

    [Fact]
    public async Task PostUpload_UnsupportedContentType_Returns415WithErrorShape()
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3]);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-msdownload");
        content.Add(fileContent, "file", "virus.exe");

        var response = await _client.PostAsync("/api/upload", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("unsupported_content_type", error!.Error);
    }

    [Fact]
    public async Task PostUpload_ExtensionDoesNotMatchDeclaredContentType_Returns400()
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3]);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        // .exe extension declared as image/png - content-type/extension mismatch.
        content.Add(fileContent, "file", "sneaky.exe");

        var response = await _client.PostAsync("/api/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("content_type_mismatch", error!.Error);
    }

    [Fact]
    public async Task PostUpload_NoFile_Returns400WithErrorShape()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("not a file"), "notAFile");

        var response = await _client.PostAsync("/api/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_request", error!.Error);
    }

    [Fact]
    public async Task PostUpload_FileOverSizeLimit_Returns413WithErrorShape()
    {
        // Between MediaEndpoints.MaxUploadSizeBytes (50 MB) and the
        // FormOptions/Kestrel overhead ceiling configured in Program.cs
        // (50 MB + 1 MB), so this deterministically exercises
        // MediaEndpoints' own file.Length check (413, {error, detail})
        // rather than the lower-level multipart/body-size cutoff -
        // see MediaEndpoints.PostUploadAsync's remarks.
        const int oversizedLength = (50 * 1024 * 1024) + (512 * 1024);
        var oversizedContent = new byte[oversizedLength];

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(oversizedContent);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", "huge.png");

        var response = await _client.PostAsync("/api/upload", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("file_too_large", error!.Error);
    }

    [Fact]
    public async Task GetMedia_ExistingFile_StreamsBackWithCorrectContentType()
    {
        using var content = new MultipartFormDataContent();
        var fileBytes = new byte[] { 1, 2, 3, 4, 5 };
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", "photo.png");
        var uploadResponse = await _client.PostAsync("/api/upload", content);
        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<UploadResponseDto>(ResponseJsonOptions);
        Assert.NotNull(uploaded);

        var fileName = uploaded!.RelativePath["_media/".Length..];
        var getResponse = await _client.GetAsync($"/media/{fileName}");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal("image/png", getResponse.Content.Headers.ContentType?.MediaType);
        var downloadedBytes = await getResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(fileBytes, downloadedBytes);
    }

    [Fact]
    public async Task GetMedia_MissingFile_Returns404WithErrorShape()
    {
        var response = await _client.GetAsync("/media/does-not-exist.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("not_found", error!.Error);
    }

    [Fact]
    public async Task GetMedia_PathTraversalAttempt_Returns400WithErrorShape()
    {
        // Same encoded-backslash technique NotesEndpointsTests uses to get
        // a literal ".." segment past ASP.NET Core's own dot-segment URL
        // normalization and into FileSystemMediaStore's own check.
        var response = await _client.GetAsync("/media/..%5coutside.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(ResponseJsonOptions);
        Assert.NotNull(error);
        Assert.Equal("invalid_path", error!.Error);
    }

    private sealed record UploadResponseDto(string RelativePath);

    private sealed record ErrorDto(string Error, string? Detail);
}
