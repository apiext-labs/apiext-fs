using Microsoft.EntityFrameworkCore;
using Apiext.Fs.Data;
using Apiext.Fs.Models.Entities;
using Apiext.Fs.Services;
using Apiext.Fs.Tests.Infrastructure;

namespace Apiext.Fs.Tests;

public sealed class BucketPolicyServiceTests
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory =
        TestFactory.CreateInMemoryDbFactory(Guid.NewGuid().ToString());

    private BucketPolicyService Sut => new(_dbFactory);

    [Fact]
    public async Task GetPolicyAsync_DefaultsToPrivate()
    {
        var policy = await Sut.GetPolicyAsync("photos", TestContext.Current.CancellationToken);

        Assert.Equal(BucketAccessPolicies.Private, policy.AccessPolicy);
        Assert.False(policy.IsPublicRead);
    }

    [Fact]
    public async Task SetAccessPolicyAsync_PublicRead_PersistsPolicy()
    {
        await Sut.SetAccessPolicyAsync("photos", BucketAccessPolicies.PublicRead, TestContext.Current.CancellationToken);

        var policy = await Sut.GetPolicyAsync("photos", TestContext.Current.CancellationToken);

        Assert.True(policy.IsPublicRead);
    }

    [Fact]
    public async Task SetAccessPolicyAsync_Private_RemovesPolicyRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await Sut.SetAccessPolicyAsync("photos", BucketAccessPolicies.PublicRead, ct);
        await Sut.SetAccessPolicyAsync("photos", BucketAccessPolicies.Private, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        Assert.Empty(await db.BucketPolicies.ToListAsync(ct));
    }

    [Fact]
    public void TryParseSupportedPolicy_PublicReadPolicy_ReturnsPublicRead()
    {
        var json = Sut.CreatePublicReadPolicyJson("photos");

        var ok = Sut.TryParseSupportedPolicy("photos", json, out var accessPolicy, out var error);

        Assert.True(ok, error);
        Assert.Equal(BucketAccessPolicies.PublicRead, accessPolicy);
    }

    [Fact]
    public void TryParseSupportedPolicy_ListBucketPolicy_IsRejected()
    {
        const string json = """
            {
              "Version": "2012-10-17",
              "Statement": [{
                "Effect": "Allow",
                "Principal": "*",
                "Action": "s3:ListBucket",
                "Resource": "arn:aws:s3:::photos"
              }]
            }
            """;

        var ok = Sut.TryParseSupportedPolicy("photos", json, out _, out var error);

        Assert.False(ok);
        Assert.Contains("public-read", error);
    }
}
