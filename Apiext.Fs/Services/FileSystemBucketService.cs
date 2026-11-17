using Apiext.Fs.Models.Api.Buckets;
using Apiext.Fs.Models.Api.Common;
using Apiext.Fs.Models.Entities;
using MimeKit;

namespace Apiext.Fs.Services;

/// <summary>
/// Bucket service backed by the native file system.
/// Each sub-directory of <c>StoreLocation</c> is treated as a bucket.
/// Files placed directly in the root are ignored.
///
/// Object metadata (ETag, content type) is served from <see cref="IObjectIndexService"/>
/// so listing does not re-hash every file. The file system stays authoritative:
/// an index entry is only used while size and last-write ticks still match.
/// </summary>
public sealed class FileSystemBucketService : IS3BucketService
{
    private readonly string _root;
    private readonly IObjectIndexService _index;
    private static readonly string Region = "us-east-1";

    public FileSystemBucketService(IConfiguration config, IObjectIndexService index)
    {
        _root = Path.GetFullPath(config["StoreLocation"]
            ?? throw new InvalidOperationException("StoreLocation is not configured."));
        _index = index;
        Directory.CreateDirectory(_root);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BucketPath(string bucket) => Path.Combine(_root, bucket);

    // Directories prefixed with a dot are reserved for application metadata
    // stored alongside the buckets (for example, Data Protection keys).
    private static bool IsInternalDirectory(DirectoryInfo directory)
        => directory.Name.StartsWith(".", StringComparison.Ordinal);

    private static S3Owner OwnerFor(string ownerId) => new() { Id = ownerId, DisplayName = ownerId };

    private static string FormatTimestamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    // ── ListBuckets ───────────────────────────────────────────────────────────

    public Task<ListBucketsResponse> ListBucketsAsync(string ownerId, CancellationToken ct = default)
    {
        var buckets = new DirectoryInfo(_root)
            .GetDirectories()
            .Where(d => !IsInternalDirectory(d))
            .Select(d => new BucketEntry
            {
                Name = d.Name,
                CreationDate = FormatTimestamp(d.CreationTimeUtc)
            })
            .ToList();

        return Task.FromResult(new ListBucketsResponse
        {
            Owner = OwnerFor(ownerId),
            Buckets = buckets
        });
    }

    // ── CreateBucket ──────────────────────────────────────────────────────────

    public Task CreateBucketAsync(string bucket, string? region, string ownerId, CancellationToken ct = default)
    {
        var path = BucketPath(bucket);
        if (Directory.Exists(path))
            throw new InvalidOperationException("BucketAlreadyOwnedByYou");

        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    // ── BucketExists ──────────────────────────────────────────────────────────

    public Task<bool> BucketExistsAsync(string bucket, CancellationToken ct = default)
        => Task.FromResult(Directory.Exists(BucketPath(bucket)));

    // ── DeleteBucket ──────────────────────────────────────────────────────────

    public async Task DeleteBucketAsync(string bucket, CancellationToken ct = default)
    {
        var path = BucketPath(bucket);
        if (!Directory.Exists(path))
            throw new KeyNotFoundException("NoSuchBucket");

        // Only directories that contain no user files (excluding multipart staging)
        var hasObjects = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
        if (hasObjects)
            throw new InvalidOperationException("BucketNotEmpty");

        Directory.Delete(path, recursive: true);
        await _index.DeleteBucketAsync(bucket, ct);
    }

    // ── GetBucketLocation ─────────────────────────────────────────────────────

    public Task<string?> GetBucketLocationAsync(string bucket, CancellationToken ct = default)
    {
        if (!Directory.Exists(BucketPath(bucket)))
            throw new KeyNotFoundException("NoSuchBucket");

        return Task.FromResult<string?>(Region);
    }

    // ── ListObjectsV2 ─────────────────────────────────────────────────────────

    public async Task<ListObjectsV2Response> ListObjectsV2Async(
        string bucket, ListObjectsV2Request request, CancellationToken ct = default)
    {
        var bucketPath = BucketPath(bucket);
        if (!Directory.Exists(bucketPath))
            throw new KeyNotFoundException("NoSuchBucket");

        var files = EnumerateFiles(bucketPath);

        var (contents, prefixes, isTruncated, nextToken) =
            ApplyListFilters(files.Keys, request.Prefix, request.Delimiter,
                request.MaxKeys, request.ContinuationToken ?? request.StartAfter,
                isContinuationToken: request.ContinuationToken != null);

        return new ListObjectsV2Response
        {
            Name = bucket,
            Prefix = request.Prefix ?? string.Empty,
            KeyCount = contents.Count,
            MaxKeys = request.MaxKeys,
            Delimiter = request.Delimiter,
            IsTruncated = isTruncated,
            NextContinuationToken = nextToken,
            EncodingType = request.EncodingType,
            Contents = await BuildContentsAsync(bucket, files, contents, request.FetchOwner, ct),
            CommonPrefixes = prefixes.Select(p => new CommonPrefix { Prefix = p }).ToList()
        };
    }

    // ── ListObjectsV1 ─────────────────────────────────────────────────────────

    public async Task<ListObjectsV1Response> ListObjectsV1Async(
        string bucket, ListObjectsV1Request request, CancellationToken ct = default)
    {
        var bucketPath = BucketPath(bucket);
        if (!Directory.Exists(bucketPath))
            throw new KeyNotFoundException("NoSuchBucket");

        var files = EnumerateFiles(bucketPath);

        var (contents, prefixes, isTruncated, nextMarker) =
            ApplyListFilters(files.Keys, request.Prefix, request.Delimiter,
                request.MaxKeys, request.Marker, isContinuationToken: false);

        return new ListObjectsV1Response
        {
            Name = bucket,
            Prefix = request.Prefix ?? string.Empty,
            MaxKeys = request.MaxKeys,
            Delimiter = request.Delimiter,
            IsTruncated = isTruncated,
            Marker = request.Marker ?? string.Empty,
            NextMarker = isTruncated ? nextMarker : null,
            EncodingType = request.EncodingType,
            Contents = await BuildContentsAsync(bucket, files, contents, fetchOwner: false, ct),
            CommonPrefixes = prefixes.Select(p => new CommonPrefix { Prefix = p }).ToList()
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Returns all object keys (relative paths using forward slashes) mapped to their
    /// file info, excluding the multipart staging directory. Keys stay sorted ordinally so
    /// marker / continuation-token paging is stable.</summary>
    private static SortedDictionary<string, FileInfo> EnumerateFiles(string bucketPath)
    {
        var stagingDir = Path.Combine(bucketPath, FileSystemMultipartService.StagingDirName);
        var map = new SortedDictionary<string, FileInfo>(StringComparer.Ordinal);

        foreach (var fi in new DirectoryInfo(bucketPath)
                     .EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (fi.FullName.StartsWith(stagingDir, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = fi.FullName[(bucketPath.Length + 1)..]
                .Replace(Path.DirectorySeparatorChar, '/');
            map[key] = fi;
        }

        return map;
    }

    private static (List<string> keys, List<string> prefixes, bool isTruncated, string? nextMarker)
        ApplyListFilters(
            IEnumerable<string> allKeys,
            string? prefix,
            string? delimiter,
            int maxKeys,
            string? afterMarker,
            bool isContinuationToken)
    {
        var keys = allKeys.AsEnumerable();

        if (!string.IsNullOrEmpty(prefix))
            keys = keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal));

        if (!string.IsNullOrEmpty(afterMarker))
            keys = isContinuationToken
                ? keys.SkipWhile(k => string.Compare(k, afterMarker, StringComparison.Ordinal) <= 0)
                : keys.Where(k => string.Compare(k, afterMarker, StringComparison.Ordinal) > 0);

        var resultKeys = new List<string>();
        var commonPrefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            if (resultKeys.Count >= maxKeys)
                return (resultKeys, commonPrefixes.ToList(), true, resultKeys[^1]);

            if (!string.IsNullOrEmpty(delimiter))
            {
                var subKey = string.IsNullOrEmpty(prefix) ? key : key[prefix.Length..];
                var delimIdx = subKey.IndexOf(delimiter, StringComparison.Ordinal);
                if (delimIdx >= 0)
                {
                    var cp = (prefix ?? string.Empty) + subKey[..(delimIdx + delimiter.Length)];
                    commonPrefixes.Add(cp);
                    continue;
                }
            }

            resultKeys.Add(key);
        }

        return (resultKeys, commonPrefixes.ToList(), false, null);
    }

    /// <summary>
    /// Builds list entries, reusing cached ETag/content type when the cached
    /// size + last-write ticks still match the file. Misses are hashed once and
    /// written back to the index in a single batch.
    /// </summary>
    private async Task<List<ObjectContent>> BuildContentsAsync(
        string bucket,
        IReadOnlyDictionary<string, FileInfo> files,
        IReadOnlyList<string> keys,
        bool fetchOwner,
        CancellationToken ct)
    {
        var cached = await _index.GetManyAsync(bucket, keys, ct);
        var pending = new List<ObjectIndexEntry>();
        var results = new List<ObjectContent>(keys.Count);

        foreach (var key in keys)
        {
            var fi = files[key];
            var ticks = fi.LastWriteTimeUtc.Ticks;
            var lastModified = FormatTimestamp(fi.LastWriteTimeUtc);

            string etag;
            if (cached.TryGetValue(key, out var entry)
                && entry.Size == fi.Length
                && entry.LastWriteUtcTicks == ticks)
            {
                etag = entry.ETag;
            }
            else
            {
                etag = ObjectETag.ComputeFromFile(fi.FullName);
                pending.Add(new ObjectIndexEntry
                {
                    Bucket = bucket,
                    Key = key,
                    Size = fi.Length,
                    LastWriteUtcTicks = ticks,
                    ETag = etag,
                    ContentType = MimeTypes.GetMimeType(fi.Name),
                    LastModified = lastModified
                });
            }

            results.Add(new ObjectContent
            {
                Key = key,
                LastModified = lastModified,
                ETag = etag,
                Size = fi.Length,
                StorageClass = S3StorageClass.Standard,
                Owner = fetchOwner ? new S3Owner { Id = "owner", DisplayName = "owner" } : null
            });
        }

        if (pending.Count > 0)
            await _index.UpsertManyAsync(pending, ct);

        return results;
    }
}
