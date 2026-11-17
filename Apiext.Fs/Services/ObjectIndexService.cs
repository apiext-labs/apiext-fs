using Microsoft.EntityFrameworkCore;
using Apiext.Fs.Data;
using Apiext.Fs.Models.Entities;

namespace Apiext.Fs.Services;

/// <summary>
/// SQLite-backed object metadata cache.
///
/// Registered as a singleton alongside the file-system S3 services, so it takes
/// <see cref="IServiceScopeFactory"/> instead of a scoped DbContext factory and
/// opens a short-lived scope per operation.
///
/// All methods are best-effort: a missing index only costs an extra hash
/// computation, so database failures are logged and swallowed rather than
/// failing S3 requests.
/// </summary>
public sealed class ObjectIndexService : IObjectIndexService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ObjectIndexService> _logger;

    public ObjectIndexService(
        IServiceScopeFactory scopeFactory,
        ILogger<ObjectIndexService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task<ObjectIndexEntry?> GetAsync(string bucket, string key, CancellationToken ct = default)
        => WithDbAsync(async db =>
            await db.ObjectIndex
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Bucket == bucket && x.Key == key, ct),
            fallback: null, bucket);

    public Task<Dictionary<string, ObjectIndexEntry>> GetManyAsync(
        string bucket, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0)
            return Task.FromResult(new Dictionary<string, ObjectIndexEntry>(StringComparer.Ordinal));

        return WithDbAsync(async db =>
        {
            var entries = await db.ObjectIndex
                .AsNoTracking()
                .Where(x => x.Bucket == bucket && keys.Contains(x.Key))
                .ToListAsync(ct);

            return entries.ToDictionary(x => x.Key, StringComparer.Ordinal);
        }, fallback: new Dictionary<string, ObjectIndexEntry>(StringComparer.Ordinal), bucket);
    }

    public Task UpsertAsync(ObjectIndexEntry entry, CancellationToken ct = default)
        => UpsertManyAsync([entry], ct);

    public Task UpsertManyAsync(
        IReadOnlyCollection<ObjectIndexEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return Task.CompletedTask;

        var bucket = entries.First().Bucket;
        var keys = entries.Select(e => e.Key).ToList();

        return WithDbAsync<bool>(async db =>
        {
            var existing = await db.ObjectIndex
                .Where(x => x.Bucket == bucket && keys.Contains(x.Key))
                .ToDictionaryAsync(x => x.Key, StringComparer.Ordinal, ct);

            foreach (var entry in entries)
            {
                if (existing.TryGetValue(entry.Key, out var row))
                {
                    row.Size = entry.Size;
                    row.LastWriteUtcTicks = entry.LastWriteUtcTicks;
                    row.ETag = entry.ETag;
                    row.ContentType = entry.ContentType;
                    row.LastModified = entry.LastModified;
                }
                else
                {
                    db.ObjectIndex.Add(entry);
                }
            }

            await db.SaveChangesAsync(ct);
            return true;
        }, fallback: false, bucket);
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken ct = default)
        => DeleteManyAsync(bucket, [key], ct);

    public Task DeleteManyAsync(
        string bucket, IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0)
            return Task.CompletedTask;

        return WithDbAsync<bool>(async db =>
        {
            if (db.Database.IsRelational())
            {
                await db.ObjectIndex
                    .Where(x => x.Bucket == bucket && keys.Contains(x.Key))
                    .ExecuteDeleteAsync(ct);
                return true;
            }

            // The in-memory provider used by tests does not support ExecuteDelete.
            var stored = await db.ObjectIndex
                .Where(x => x.Bucket == bucket && keys.Contains(x.Key))
                .ToListAsync(ct);
            db.ObjectIndex.RemoveRange(stored);
            await db.SaveChangesAsync(ct);
            return true;
        }, fallback: false, bucket);
    }

    public Task DeleteBucketAsync(string bucket, CancellationToken ct = default)
        => WithDbAsync<bool>(async db =>
        {
            if (db.Database.IsRelational())
            {
                await db.ObjectIndex.Where(x => x.Bucket == bucket).ExecuteDeleteAsync(ct);
                return true;
            }

            var stored = await db.ObjectIndex.Where(x => x.Bucket == bucket).ToListAsync(ct);
            db.ObjectIndex.RemoveRange(stored);
            await db.SaveChangesAsync(ct);
            return true;
        }, fallback: false, bucket);

    /// <summary>
    /// Runs <paramref name="action"/> against a freshly scoped DbContext.
    /// Any failure is logged and <paramref name="fallback"/> is returned so the
    /// S3 request can continue using the file system alone.
    /// </summary>
    private async Task<T> WithDbAsync<T>(
        Func<AppDbContext, Task<T>> action, T fallback, string bucket)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            return await action(db);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Object index operation failed for bucket {Bucket}", bucket);
            return fallback;
        }
    }
}
