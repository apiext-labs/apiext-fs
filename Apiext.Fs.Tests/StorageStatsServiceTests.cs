using System.Globalization;
using Apiext.Fs.Services;
using Apiext.Fs.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apiext.Fs.Tests;

public sealed class StorageStatsServiceTests : IDisposable
{
    private readonly TempDirectory _tmp = new();

    public void Dispose() => _tmp.Dispose();

    /// <summary>Watcher is opt-in for tests so cache behaviour stays deterministic.</summary>
    private StorageStatsService CreateService(int? cacheSeconds = null, bool watchChanges = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["StoreLocation"] = _tmp.Path,
            ["StorageStats:WatchChanges"] = watchChanges ? "true" : "false"
        };

        if (cacheSeconds.HasValue)
            values["StorageStats:CacheSeconds"] = cacheSeconds.Value.ToString(CultureInfo.InvariantCulture);

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new StorageStatsService(config, NullLogger<StorageStatsService>.Instance);
    }

    private static void WriteFile(string directory, string relativePath, int size)
    {
        var full = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    [Fact]
    public async Task GetAsync_EmptyStore_ReturnsZeroTotals()
    {
        using var sut = CreateService();
        var stats = await sut.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, stats.BucketCount);
        Assert.Equal(0, stats.ObjectCount);
        Assert.Equal(0, stats.TotalBytes);
        Assert.Empty(stats.Buckets);
        Assert.Null(stats.LastModifiedUtc);
    }

    [Fact]
    public async Task GetAsync_CountsObjectsAndBytesPerBucket()
    {
        using var sut = CreateService();
        var alpha = _tmp.Sub("alpha");
        WriteFile(alpha, "one.txt", 10);
        WriteFile(alpha, "nested/two.txt", 32);

        var beta = _tmp.Sub("beta");
        WriteFile(beta, "three.txt", 5);

        var stats = await sut.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, stats.BucketCount);
        Assert.Equal(3, stats.ObjectCount);
        Assert.Equal(47, stats.TotalBytes);

        var alphaStats = Assert.Single(stats.Buckets, b => b.Name == "alpha");
        Assert.Equal(2, alphaStats.ObjectCount);
        Assert.Equal(42, alphaStats.TotalBytes);
        Assert.NotNull(alphaStats.LastModifiedUtc);
    }

    [Fact]
    public async Task GetAsync_IgnoresDotDirectoriesAndMultipartStaging()
    {
        using var sut = CreateService();
        var bucket = _tmp.Sub("bucket");
        WriteFile(bucket, "keep.txt", 8);
        WriteFile(bucket, FileSystemMultipartService.StagingDirName + "/upload1/part1", 100);

        var internalDir = _tmp.Sub(".dataprotection");
        WriteFile(internalDir, "key.xml", 200);

        var stats = await sut.GetAsync(TestContext.Current.CancellationToken);

        var only = Assert.Single(stats.Buckets);
        Assert.Equal("bucket", only.Name);
        Assert.Equal(1, only.ObjectCount);
        Assert.Equal(8, only.TotalBytes);
        Assert.Equal(8, stats.TotalBytes);
    }

    [Fact]
    public async Task GetAsync_OrdersBucketsBySizeDescending()
    {
        using var sut = CreateService();
        WriteFile(_tmp.Sub("small"), "a.bin", 4);
        WriteFile(_tmp.Sub("large"), "b.bin", 64);
        WriteFile(_tmp.Sub("medium"), "c.bin", 16);

        var stats = await sut.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "large", "medium", "small" }, stats.Buckets.Select(b => b.Name).ToArray());
    }

    [Fact]
    public async Task GetAsync_ReusesCachedResult_UntilInvalidated()
    {
        using var sut = CreateService(cacheSeconds: 60);
        var bucket = _tmp.Sub("bucket");
        WriteFile(bucket, "a.txt", 3);

        var first = await sut.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, first.ObjectCount);

        WriteFile(bucket, "b.txt", 4);
        var cached = await sut.GetAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, cached);
        Assert.Equal(1, cached.ObjectCount);

        sut.Invalidate();
        var afterInvalidate = await sut.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, afterInvalidate.ObjectCount);
        Assert.Equal(7, afterInvalidate.TotalBytes);
    }

    [Fact]
    public async Task RefreshAsync_ForcesRescan()
    {
        using var sut = CreateService(cacheSeconds: 60);
        var bucket = _tmp.Sub("bucket");
        WriteFile(bucket, "a.txt", 3);

        var first = await sut.GetAsync(TestContext.Current.CancellationToken);
        WriteFile(bucket, "b.txt", 4);

        var refreshed = await sut.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, refreshed.ObjectCount);

        // The forced scan also repopulates the cache.
        Assert.Same(refreshed, await sut.GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_RescansAfterCacheExpires()
    {
        using var sut = CreateService(cacheSeconds: 1);
        var bucket = _tmp.Sub("bucket");
        WriteFile(bucket, "a.txt", 3);
        Assert.Equal(1, (await sut.GetAsync(TestContext.Current.CancellationToken)).ObjectCount);

        WriteFile(bucket, "b.txt", 4);
        await Task.Delay(1200, TestContext.Current.CancellationToken);

        Assert.Equal(2, (await sut.GetAsync(TestContext.Current.CancellationToken)).ObjectCount);
    }

    [Fact]
    public async Task GetAsync_InvalidatedByFileSystemWatcher()
    {
        using var sut = CreateService(cacheSeconds: 600, watchChanges: true);
        var bucket = _tmp.Sub("bucket");
        WriteFile(bucket, "a.txt", 3);
        Assert.Equal(1, (await sut.GetAsync(TestContext.Current.CancellationToken)).ObjectCount);

        WriteFile(bucket, "b.txt", 4);

        var updated = false;
        for (var i = 0; i < 40 && !updated; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            updated = (await sut.GetAsync(TestContext.Current.CancellationToken)).ObjectCount == 2;
        }

        Assert.True(updated, "存储目录变化后缓存应在数秒内失效");
    }
}
