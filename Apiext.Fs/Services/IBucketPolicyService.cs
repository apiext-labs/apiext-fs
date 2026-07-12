namespace Apiext.Fs.Services;

public sealed class BucketPolicyDto
{
    public string BucketName { get; init; } = string.Empty;
    public string AccessPolicy { get; init; } = Models.Entities.BucketAccessPolicies.Private;
    public bool IsPublicRead => AccessPolicy == Models.Entities.BucketAccessPolicies.PublicRead;
}

public interface IBucketPolicyService
{
    Task<BucketPolicyDto> GetPolicyAsync(string bucketName, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, BucketPolicyDto>> GetPoliciesAsync(IEnumerable<string> bucketNames, CancellationToken ct = default);
    Task SetAccessPolicyAsync(string bucketName, string accessPolicy, CancellationToken ct = default);
    Task<bool> IsPublicReadAsync(string bucketName, CancellationToken ct = default);
    string CreatePublicReadPolicyJson(string bucketName);
    bool TryParseSupportedPolicy(string bucketName, string json, out string accessPolicy, out string? error);
}
