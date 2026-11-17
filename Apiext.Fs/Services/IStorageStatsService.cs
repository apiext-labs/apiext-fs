namespace Apiext.Fs.Services;

/// <summary>
/// Aggregated storage figures for the dashboard. Every value is derived from the
/// file system (the single source of truth) rather than from the object index.
/// Results are cached by <see cref="IStorageStatsService"/> implementations.
/// </summary>
public sealed class StorageStats
{
    public int BucketCount { get; init; }
    public long ObjectCount { get; init; }
    public long TotalBytes { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
    public IReadOnlyList<BucketStats> Buckets { get; init; } = [];
}

/// <summary>Per-bucket part of <see cref="StorageStats"/>.</summary>
public sealed class BucketStats
{
    public string Name { get; init; } = string.Empty;
    public long ObjectCount { get; init; }
    public long TotalBytes { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
}

public interface IStorageStatsService
{
    /// <summary>
    /// Returns cached totals, rescanning only when the cache expired or the store changed.
    /// Buckets are sorted by size for the returned value.
    /// </summary>
    Task<StorageStats> GetAsync(CancellationToken ct = default);

    /// <summary>Forces a rescan and repopulates the cache (dashboard "refresh" action).</summary>
    Task<StorageStats> RefreshAsync(CancellationToken ct = default);

    /// <summary>Marks the cache stale so the next <see cref="GetAsync"/> rescans.</summary>
    void Invalidate();
}
