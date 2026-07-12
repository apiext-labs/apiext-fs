using Microsoft.AspNetCore.Http;
using Apiext.Fs.Middleware;
using Apiext.Fs.Models.Entities;
using Apiext.Fs.Services;

namespace Apiext.Fs.Tests;

public sealed class S3AuthMiddlewarePublicReadTests
{
    [Fact]
    public async Task InvokeAsync_PublicReadObjectGetWithoutCredentials_AllowsRequest()
    {
        var called = false;
        var context = ContextFor("GET", "/photos/cat.jpg");
        var sut = new S3AuthMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(
            context,
            new FakeAuthorizationProvider(),
            new FakeBucketPolicyService(publicBuckets: ["photos"]),
            new FakePresignedUrlService());

        Assert.True(called);
        Assert.IsType<S3CallerInfo>(context.Items["S3Caller"]);
    }

    [Fact]
    public async Task InvokeAsync_PublicReadBucketListWithoutCredentials_IsDenied()
    {
        var called = false;
        var context = ContextFor("GET", "/photos");
        var sut = new S3AuthMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(
            context,
            new FakeAuthorizationProvider(),
            new FakeBucketPolicyService(publicBuckets: ["photos"]),
            new FakePresignedUrlService());

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_PublicReadObjectGetWithMalformedAuthorization_IsDenied()
    {
        var called = false;
        var context = ContextFor("GET", "/photos/cat.jpg");
        context.Request.Headers.Authorization = "Bad value";
        var sut = new S3AuthMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(
            context,
            new FakeAuthorizationProvider(),
            new FakeBucketPolicyService(publicBuckets: ["photos"]),
            new FakePresignedUrlService());

        Assert.False(called);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public void IsObjectReadRequest_GetBucketWithListType_IsFalse()
    {
        var context = ContextFor("GET", "/photos");
        context.Request.QueryString = new QueryString("?list-type=2");

        Assert.False(S3AuthMiddleware.IsObjectReadRequest(context.Request, out _));
    }

    private static DefaultHttpContext ContextFor(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.Items["x-amz-request-id"] = "test-request";
        context.Items["x-amz-id-2"] = "test-host";
        return context;
    }

    private sealed class FakeAuthorizationProvider : IAuthorizationProvider
    {
        public CallerIdentity Anonymous { get; } = new()
        {
            UserId = "anonymous",
            Username = "anonymous",
            IsSuperAdmin = false
        };

        public Task<CallerIdentity?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
            => Task.FromResult<CallerIdentity?>(null);

        public Task<AclCheckResult> CheckAccessAsync(
            CallerIdentity caller, string bucketName, BucketPermission permission, CancellationToken ct = default)
            => Task.FromResult(AclCheckResult.Denied);

        public Task<(CallerIdentity identity, string s3Secret)?> FindS3CredentialAsync(
            string accessKeyId, CancellationToken ct = default)
            => Task.FromResult<(CallerIdentity identity, string s3Secret)?>(null);
    }

    private sealed class FakeBucketPolicyService(IEnumerable<string> publicBuckets) : IBucketPolicyService
    {
        private readonly HashSet<string> _publicBuckets = new(publicBuckets, StringComparer.Ordinal);

        public Task<BucketPolicyDto> GetPolicyAsync(string bucketName, CancellationToken ct = default)
            => Task.FromResult(new BucketPolicyDto
            {
                BucketName = bucketName,
                AccessPolicy = _publicBuckets.Contains(bucketName)
                    ? BucketAccessPolicies.PublicRead
                    : BucketAccessPolicies.Private
            });

        public Task<IReadOnlyDictionary<string, BucketPolicyDto>> GetPoliciesAsync(
            IEnumerable<string> bucketNames, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, BucketPolicyDto>>(
                bucketNames.ToDictionary(
                    b => b,
                    b => new BucketPolicyDto
                    {
                        BucketName = b,
                        AccessPolicy = _publicBuckets.Contains(b)
                            ? BucketAccessPolicies.PublicRead
                            : BucketAccessPolicies.Private
                    },
                    StringComparer.Ordinal));

        public Task SetAccessPolicyAsync(string bucketName, string accessPolicy, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> IsPublicReadAsync(string bucketName, CancellationToken ct = default)
            => Task.FromResult(_publicBuckets.Contains(bucketName));

        public string CreatePublicReadPolicyJson(string bucketName) => "{}";

        public bool TryParseSupportedPolicy(string bucketName, string json, out string accessPolicy, out string? error)
        {
            accessPolicy = BucketAccessPolicies.Private;
            error = null;
            return false;
        }
    }

    private sealed class FakePresignedUrlService : IPresignedUrlService
    {
        public PresignedToken Create(
            string method, string bucket, string key, string userId, string username,
            bool isSuperAdmin, int expiresInSeconds = 3600)
            => throw new NotSupportedException();

        public PresignedToken? Validate(string token, string method, string bucket, string key) => null;

        public void Purge() { }
    }
}
