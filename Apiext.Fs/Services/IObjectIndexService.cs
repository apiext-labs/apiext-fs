using Apiext.Fs.Models.Entities;

namespace Apiext.Fs.Services;

/// <summary>
/// Metadata index (cache) for stored objects. The file system remains the
/// source of truth; this index only avoids re-reading file content to compute
/// ETags and keeps ListObjects / GetObject consistent.
/// </summary>
public interface IObjectIndexService
{
    /// <summary>Returns the cached entry if one exists (no freshness check).</summary>
    Task<ObjectIndexEntry?> GetAsync(string bucket, string key, CancellationToken ct = default);

    /// <summary>Returns cached entries for the requested keys.</summary>
    Task<Dictionary<string, ObjectIndexEntry>> GetManyAsync(
        string bucket, IReadOnlyCollection<string> keys, CancellationToken ct = default);

    Task UpsertAsync(ObjectIndexEntry entry, CancellationToken ct = default);

    Task UpsertManyAsync(IReadOnlyCollection<ObjectIndexEntry> entries, CancellationToken ct = default);

    Task DeleteAsync(string bucket, string key, CancellationToken ct = default);

    Task DeleteManyAsync(string bucket, IReadOnlyCollection<string> keys, CancellationToken ct = default);

    /// <summary>Drops every cached entry belonging to a bucket.</summary>
    Task DeleteBucketAsync(string bucket, CancellationToken ct = default);
}
