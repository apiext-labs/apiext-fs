using System.Diagnostics;

namespace Apiext.Fs.Services;

/// <summary>
/// File-system based implementation of <see cref="IStorageStatsService"/>.
/// Directories under <c>StoreLocation</c> are buckets; dot-directories are
/// application metadata and are skipped, as is the multipart staging folder.
/// </summary>
/// <remarks>
/// A full scan walks every object, so results are cached for
/// <c>StorageStats:CacheSeconds</c> (30s by default). A <see cref="FileSystemWatcher"/>
/// marks the cache stale on store changes, so uploads / deletes show up immediately;
/// if the watcher cannot be created (e.g. an unsupported network mount), the TTL is
/// the fallback. Concurrency is collapsed to a single scan via a semaphore.
/// </remarks>
public sealed class StorageStatsService : IStorageStatsService, IDisposable
{
    private const int DefaultCacheSeconds = 30;

    private sealed record CacheEntry(StorageStats Stats, long TimestampMs);

    private readonly string _root;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<StorageStatsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly FileSystemWatcher? _watcher;
    private readonly string _stagingSegment = $"{Path.DirectorySeparatorChar}{FileSystemMultipartService.StagingDirName}{Path.DirectorySeparatorChar}";

    private CacheEntry? _entry;
    private int _dirty = 1;

    public StorageStatsService(IConfiguration config, ILogger<StorageStatsService> logger)
    {
        _root = Path.GetFullPath(config["StoreLocation"]
            ?? throw new InvalidOperationException("StoreLocation is not configured."));
        _logger = logger;

        var seconds = config.GetValue("StorageStats:CacheSeconds", DefaultCacheSeconds);
        _cacheTtl = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

        if (config.GetValue("StorageStats:WatchChanges", true))
            _watcher = TryCreateWatcher();
    }

    public Task<StorageStats> GetAsync(CancellationToken ct = default)
        => GetCoreAsync(force: false, ct);

    public Task<StorageStats> RefreshAsync(CancellationToken ct = default)
        => GetCoreAsync(force: true, ct);

    public void Invalidate() => Interlocked.Exchange(ref _dirty, 1);

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        _gate.Dispose();
    }

    private async Task<StorageStats> GetCoreAsync(bool force, CancellationToken ct)
    {
        if (!force && Cached() is { } cached)
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed the cache while we were waiting.
            if (!force && Cached() is { } refreshed)
                return refreshed;

            // Consume the stale flag before scanning: changes that arrive during
            // the scan set it again and make the next call rescan.
            Interlocked.Exchange(ref _dirty, 0);

            // Directory enumeration is blocking; keep it off the renderer thread.
            var stats = await Task.Run(() => Scan(ct), ct);
            Volatile.Write(ref _entry, new CacheEntry(stats, _clock.ElapsedMilliseconds));
            return stats;
        }
        finally
        {
            _gate.Release();
        }
    }

    private StorageStats? Cached()
    {
        var entry = Volatile.Read(ref _entry);
        if (entry is null || Volatile.Read(ref _dirty) != 0 || _cacheTtl <= TimeSpan.Zero)
            return null;

        return _clock.ElapsedMilliseconds - entry.TimestampMs < _cacheTtl.TotalMilliseconds
            ? entry.Stats
            : null;
    }

    private FileSystemWatcher? TryCreateWatcher()
    {
        try
        {
            var watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024
            };

            watcher.Created += OnStoreChanged;
            watcher.Deleted += OnStoreChanged;
            watcher.Changed += OnStoreChanged;
            watcher.Renamed += (_, _) => Invalidate();
            watcher.Error += (_, _) => Invalidate();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex)
        {
            // TTL-only mode is still correct, just less fresh.
            _logger.LogWarning(ex, "无法监听存储目录 {Root} 的变化，统计缓存将仅按有效期刷新。", _root);
            return null;
        }
    }

    private void OnStoreChanged(object sender, FileSystemEventArgs e)
    {
        // Chunk uploads churn inside the staging folder; the final move into the
        // object path raises an event that is not filtered out.
        if (e.FullPath.Contains(_stagingSegment, StringComparison.OrdinalIgnoreCase))
            return;

        Invalidate();
    }

    private StorageStats Scan(CancellationToken ct)
    {
        var root = new DirectoryInfo(_root);
        if (!root.Exists)
            return new StorageStats();

        var stagingPrefix = FileSystemMultipartService.StagingDirName + "/";

        var buckets = new List<BucketStats>();
        long totalObjects = 0;
        long totalBytes = 0;
        DateTime? lastModified = null;

        foreach (var dir in root.EnumerateDirectories()
                     .Where(d => !d.Name.StartsWith(".", StringComparison.Ordinal))
                     .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            long objects = 0;
            long bytes = 0;
            DateTime? bucketLastModified = null;

            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var key = file.FullName[(dir.FullName.Length + 1)..]
                    .Replace(Path.DirectorySeparatorChar, '/');

                // In-flight multipart uploads are not objects yet.
                if (key.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                objects++;
                bytes += file.Length;

                if (bucketLastModified is null || file.LastWriteTimeUtc > bucketLastModified)
                    bucketLastModified = file.LastWriteTimeUtc;
            }

            buckets.Add(new BucketStats
            {
                Name = dir.Name,
                ObjectCount = objects,
                TotalBytes = bytes,
                LastModifiedUtc = bucketLastModified
            });

            totalObjects += objects;
            totalBytes += bytes;

            if (bucketLastModified is not null
                && (lastModified is null || bucketLastModified > lastModified))
            {
                lastModified = bucketLastModified;
            }
        }

        return new StorageStats
        {
            BucketCount = buckets.Count,
            ObjectCount = totalObjects,
            TotalBytes = totalBytes,
            LastModifiedUtc = lastModified,
            Buckets = buckets.OrderByDescending(b => b.TotalBytes)
                             .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                             .ToList()
        };
    }
}
