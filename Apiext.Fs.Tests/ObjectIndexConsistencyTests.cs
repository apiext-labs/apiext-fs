using System.Security.Cryptography;
using System.Text;
using Apiext.Fs.Models.Api.Buckets;
using Apiext.Fs.Models.Api.Objects;
using Apiext.Fs.Services;
using Apiext.Fs.Tests.Infrastructure;

namespace Apiext.Fs.Tests;

/// <summary>
/// Verifies that ListObjects and GetObject/HeadObject report the same ETag for
/// the same file, and that the metadata index self-heals when files change on
/// disk behind the service's back.
/// </summary>
public sealed class ObjectIndexConsistencyTests : IDisposable
{
    private readonly TempDirectory _tmp = new();
    private readonly FileSystemBucketService _buckets;
    private readonly FileSystemObjectService _objects;

    public ObjectIndexConsistencyTests()
    {
        var config = TestFactory.ConfigFor(_tmp.Path);
        var index = TestFactory.CreateIndex($"{nameof(ObjectIndexConsistencyTests)}-{Guid.NewGuid():N}");
        _buckets = new FileSystemBucketService(config, index);
        _objects = new FileSystemObjectService(config, index);
    }

    public void Dispose() => _tmp.Dispose();

    private static MemoryStream Body(string text) => new(Encoding.UTF8.GetBytes(text));

    private async Task CreateBucketAsync()
        => await _buckets.CreateBucketAsync("bucket", null, "user", TestContext.Current.CancellationToken);

    private async Task<ObjectContent> ListSingleAsync()
    {
        var list = await _buckets.ListObjectsV2Async(
            "bucket", new ListObjectsV2Request { MaxKeys = 100 }, TestContext.Current.CancellationToken);
        return Assert.Single(list.Contents);
    }

    [Fact]
    public async Task ListObjects_ETagEqualsPutAndHead()
    {
        await CreateBucketAsync();
        var ct = TestContext.Current.CancellationToken;

        var put = await _objects.PutObjectAsync(
            new PutObjectRequest { Bucket = "bucket", Key = "hello.txt" }, Body("hello world"), ct);

        var listed = await ListSingleAsync();
        var head = await _objects.HeadObjectAsync(
            new GetObjectRequest { Bucket = "bucket", Key = "hello.txt" }, ct);

        Assert.Equal(put.ETag, listed.ETag);
        Assert.Equal(put.ETag, head!.ETag);
    }

    [Fact]
    public async Task ListObjects_ETagIsContentMd5()
    {
        await CreateBucketAsync();
        var ct = TestContext.Current.CancellationToken;
        var payload = "content-md5-check";

        await _objects.PutObjectAsync(
            new PutObjectRequest { Bucket = "bucket", Key = "md5.txt" }, Body(payload), ct);

        var expected = $"\"{Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()}\"";
        var listed = await ListSingleAsync();

        Assert.Equal(expected, listed.ETag);
    }

    [Fact]
    public async Task ListObjects_AfterExternalFileChange_RefreshesETagAndStillMatchesGet()
    {
        await CreateBucketAsync();
        var ct = TestContext.Current.CancellationToken;

        await _objects.PutObjectAsync(
            new PutObjectRequest { Bucket = "bucket", Key = "data.txt" }, Body("v1"), ct);
        var before = await ListSingleAsync();

        // Simulate an out-of-band edit (file manager / rsync) with a different size.
        var filePath = Path.Combine(_tmp.Path, "bucket", "data.txt");
        await File.WriteAllTextAsync(filePath, "version-2-longer", ct);
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(1));

        var after = await ListSingleAsync();
        var head = await _objects.HeadObjectAsync(
            new GetObjectRequest { Bucket = "bucket", Key = "data.txt" }, ct);

        Assert.NotEqual(before.ETag, after.ETag);
        Assert.Equal(after.ETag, head!.ETag);
        Assert.Equal(Encoding.UTF8.GetByteCount("version-2-longer"), after.Size);
    }

    [Fact]
    public async Task ListObjects_AfterDeleteAndReupload_ReportsNewETag()
    {
        await CreateBucketAsync();
        var ct = TestContext.Current.CancellationToken;

        await _objects.PutObjectAsync(
            new PutObjectRequest { Bucket = "bucket", Key = "cycle.txt" }, Body("first"), ct);
        var first = await ListSingleAsync();

        await _objects.DeleteObjectAsync(
            new DeleteObjectRequest { Bucket = "bucket", Key = "cycle.txt" }, ct);

        await _objects.PutObjectAsync(
            new PutObjectRequest { Bucket = "bucket", Key = "cycle.txt" }, Body("second-content"), ct);
        var second = await ListSingleAsync();

        Assert.NotEqual(first.ETag, second.ETag);
    }

    [Fact]
    public async Task HeadAndList_UseCachedETag_WhenFileIsUnchanged()
    {
        await CreateBucketAsync();
        var ct = TestContext.Current.CancellationToken;

        await _objects.PutObjectAsync(
            new PutObjectRequest { Bucket = "bucket", Key = "stable.txt" }, Body("stable"), ct);

        var first = await ListSingleAsync();
        var second = await ListSingleAsync();
        var head = await _objects.HeadObjectAsync(
            new GetObjectRequest { Bucket = "bucket", Key = "stable.txt" }, ct);

        Assert.Equal(first.ETag, second.ETag);
        Assert.Equal(first.ETag, head!.ETag);
    }
}
