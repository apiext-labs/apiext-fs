using Microsoft.AspNetCore.Mvc;
using Apiext.Fs.Models.Api.Buckets;
using Apiext.Fs.Middleware;
using Apiext.Fs.Services;

namespace Apiext.Fs.Controllers;

/// <summary>
/// Handles S3-compatible bucket-level operations (Phase 1).
///
/// Route disambiguation:
///   GET  /          → ListBuckets
///   PUT  /{bucket}  → CreateBucket
///   DELETE /{bucket}→ DeleteBucket
///   HEAD /{bucket}  → HeadBucket
///   GET  /{bucket}?location     → GetBucketLocation
///   GET  /{bucket}?list-type=2  → ListObjectsV2
///   GET  /{bucket}              → ListObjectsV1
/// </summary>
[ApiController]
public sealed class BucketController : S3ControllerBase
{
    private readonly IS3BucketService _buckets;
    private readonly IBucketPolicyService _bucketPolicies;

    public BucketController(
        IS3BucketService buckets,
        IBucketPolicyService bucketPolicies,
        IS3XmlSerializer xml) : base(xml)
    {
        _buckets = buckets;
        _bucketPolicies = bucketPolicies;
    }

    // ── ListBuckets ───────────────────────────────────────────────────────────

    /// <summary>GET /</summary>
    [HttpGet("/")]
    public async Task<IActionResult> ListBuckets(CancellationToken ct)
    {
        var result = await _buckets.ListBucketsAsync(GetOwnerId(), ct);
        return XmlOk(result);
    }

    // ── CreateBucket ──────────────────────────────────────────────────────────

    /// <summary>PUT /{bucket}</summary>
    [HttpPut("/{bucket}")]
    public async Task<IActionResult> CreateBucket(string bucket, CancellationToken ct)
    {
        if (Request.Query.ContainsKey("policy"))
            return await PutBucketPolicy(bucket, ct);

        CreateBucketRequest? body = null;
        if (Request.ContentLength > 0)
        {
            var requestBody = S3ChunkedDecodingStream.IsAwsChunked(Request.Headers)
                ? new S3ChunkedDecodingStream(Request.Body)
                : Request.Body;

            body = await Xml.DeserializeAsync<CreateBucketRequest>(requestBody, ct);
            if (body is null)
                return MalformedXml();
        }

        try
        {
            await _buckets.CreateBucketAsync(bucket, body?.LocationConstraint, GetOwnerId(), ct);
        }
        catch (InvalidOperationException ex) when (ex.Message == "BucketAlreadyOwnedByYou")
        {
            return S3Error("BucketAlreadyOwnedByYou",
                "Your previous request to create the named bucket succeeded and you already own it.",
                StatusCodes.Status409Conflict, bucketName: bucket);
        }
        catch (InvalidOperationException ex) when (ex.Message == "BucketAlreadyExists")
        {
            return S3Error("BucketAlreadyExists",
                "The requested bucket name is not available.",
                StatusCodes.Status409Conflict, bucketName: bucket);
        }

        Response.Headers.Location = $"/{bucket}";
        return Ok();
    }

    // ── HeadBucket ────────────────────────────────────────────────────────────

    /// <summary>HEAD /{bucket}</summary>
    [HttpHead("/{bucket}")]
    public async Task<IActionResult> HeadBucket(string bucket, CancellationToken ct)
    {
        if (!await _buckets.BucketExistsAsync(bucket, ct))
            return NotFound();
        return Ok();
    }

    // ── DeleteBucket ──────────────────────────────────────────────────────────

    /// <summary>DELETE /{bucket}</summary>
    [HttpDelete("/{bucket}")]
    public async Task<IActionResult> DeleteBucket(string bucket, CancellationToken ct)
    {
        if (Request.Query.ContainsKey("policy"))
            return await DeleteBucketPolicy(bucket, ct);

        if (!await _buckets.BucketExistsAsync(bucket, ct))
            return NoSuchBucket(bucket);

        try
        {
            await _buckets.DeleteBucketAsync(bucket, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message == "BucketNotEmpty")
        {
            return S3Error("BucketNotEmpty",
                "The bucket you tried to delete is not empty.",
                StatusCodes.Status409Conflict, bucketName: bucket);
        }

        return NoContent();
    }

    // ── GetBucketLocation ─────────────────────────────────────────────────────

    /// <summary>GET /{bucket}?location</summary>
    [HttpGet("/{bucket}")]
    [ActionName(nameof(GetBucketLocation))]
    public async Task<IActionResult> GetBucketLocation(string bucket, CancellationToken ct,
        [FromQuery(Name = "location")] string? location = null)
    {
        if (Request.Query.ContainsKey("policy"))
            return await GetBucketPolicy(bucket, ct);

        if (location is null)
            return await ListObjects(bucket, ct);   // fall through to listing

        if (!await _buckets.BucketExistsAsync(bucket, ct))
            return NoSuchBucket(bucket);

        var region = await _buckets.GetBucketLocationAsync(bucket, ct);
        var response = new GetBucketLocationResponse { Region = region };
        return XmlOk(response);
    }

    // ── ListObjectsV2 / ListObjectsV1 ─────────────────────────────────────────

    private async Task<IActionResult> ListObjects(string bucket, CancellationToken ct)
    {
        if (!await _buckets.BucketExistsAsync(bucket, ct))
            return NoSuchBucket(bucket);

        var q = Request.Query;

        // V2 when list-type=2 is present
        if (q.TryGetValue("list-type", out var listType) && listType == "2")
        {
            var req = new ListObjectsV2Request
            {
                Prefix = q["prefix"],
                Delimiter = q["delimiter"],
                MaxKeys = int.TryParse(q["max-keys"], out var mk) ? Math.Clamp(mk, 1, 1000) : 1000,
                ContinuationToken = q["continuation-token"],
                StartAfter = q["start-after"],
                FetchOwner = q["fetch-owner"] == "true",
                EncodingType = q["encoding-type"]
            };
            var result = await _buckets.ListObjectsV2Async(bucket, req, ct);
            return XmlOk(result);
        }
        else
        {
            var req = new ListObjectsV1Request
            {
                Prefix = q["prefix"],
                Delimiter = q["delimiter"],
                MaxKeys = int.TryParse(q["max-keys"], out var mk) ? Math.Clamp(mk, 1, 1000) : 1000,
                Marker = q["marker"],
                EncodingType = q["encoding-type"]
            };
            var result = await _buckets.ListObjectsV1Async(bucket, req, ct);
            return XmlOk(result);
        }
    }

    private async Task<IActionResult> GetBucketPolicy(string bucket, CancellationToken ct)
    {
        if (!IsSuperAdminCaller())
            return AccessDenied();

        if (!await _buckets.BucketExistsAsync(bucket, ct))
            return NoSuchBucket(bucket);

        var policy = await _bucketPolicies.GetPolicyAsync(bucket, ct);
        if (!policy.IsPublicRead)
        {
            return S3Error("NoSuchBucketPolicy",
                "The bucket policy does not exist.",
                StatusCodes.Status404NotFound,
                bucketName: bucket);
        }

        return Content(_bucketPolicies.CreatePublicReadPolicyJson(bucket), "application/json");
    }

    private async Task<IActionResult> PutBucketPolicy(string bucket, CancellationToken ct)
    {
        if (!IsSuperAdminCaller())
            return AccessDenied();

        if (!await _buckets.BucketExistsAsync(bucket, ct))
            return NoSuchBucket(bucket);

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (!_bucketPolicies.TryParseSupportedPolicy(bucket, body, out var accessPolicy, out var error))
        {
            return S3Error("MalformedPolicy",
                error ?? "The bucket policy is malformed or unsupported.",
                StatusCodes.Status400BadRequest,
                bucketName: bucket);
        }

        await _bucketPolicies.SetAccessPolicyAsync(bucket, accessPolicy, ct);
        return NoContent();
    }

    private async Task<IActionResult> DeleteBucketPolicy(string bucket, CancellationToken ct)
    {
        if (!IsSuperAdminCaller())
            return AccessDenied();

        if (!await _buckets.BucketExistsAsync(bucket, ct))
            return NoSuchBucket(bucket);

        await _bucketPolicies.SetAccessPolicyAsync(bucket, Models.Entities.BucketAccessPolicies.Private, ct);
        return NoContent();
    }

    private bool IsSuperAdminCaller()
        => HttpContext.Items["S3Caller"] is S3CallerInfo { IsSuperAdmin: true };

    private ContentResult AccessDenied()
        => S3Error("AccessDenied", "Access denied.", StatusCodes.Status403Forbidden);
}
