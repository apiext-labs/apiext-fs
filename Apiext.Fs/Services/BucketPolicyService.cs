using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Apiext.Fs.Data;
using Apiext.Fs.Models.Entities;

namespace Apiext.Fs.Services;

public sealed class BucketPolicyService : IBucketPolicyService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public BucketPolicyService(IDbContextFactory<AppDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<BucketPolicyDto> GetPolicyAsync(string bucketName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.BucketPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.BucketName == bucketName, ct);

        return new BucketPolicyDto
        {
            BucketName = bucketName,
            AccessPolicy = row?.AccessPolicy ?? BucketAccessPolicies.Private
        };
    }

    public async Task<IReadOnlyDictionary<string, BucketPolicyDto>> GetPoliciesAsync(
        IEnumerable<string> bucketNames, CancellationToken ct = default)
    {
        var names = bucketNames.Distinct(StringComparer.Ordinal).ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.BucketPolicies
            .AsNoTracking()
            .Where(p => names.Contains(p.BucketName))
            .ToDictionaryAsync(p => p.BucketName, p => p.AccessPolicy, StringComparer.Ordinal, ct);

        return names.ToDictionary(
            name => name,
            name => new BucketPolicyDto
            {
                BucketName = name,
                AccessPolicy = rows.TryGetValue(name, out var policy)
                    ? policy
                    : BucketAccessPolicies.Private
            },
            StringComparer.Ordinal);
    }

    public async Task SetAccessPolicyAsync(string bucketName, string accessPolicy, CancellationToken ct = default)
    {
        if (accessPolicy is not BucketAccessPolicies.Private and not BucketAccessPolicies.PublicRead)
            throw new ArgumentOutOfRangeException(nameof(accessPolicy), accessPolicy, "Unsupported bucket access policy.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.BucketPolicies.FirstOrDefaultAsync(p => p.BucketName == bucketName, ct);

        if (accessPolicy == BucketAccessPolicies.Private)
        {
            if (row is not null)
                db.BucketPolicies.Remove(row);
        }
        else if (row is null)
        {
            db.BucketPolicies.Add(new BucketPolicy
            {
                BucketName = bucketName,
                AccessPolicy = accessPolicy
            });
        }
        else
        {
            row.AccessPolicy = accessPolicy;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsPublicReadAsync(string bucketName, CancellationToken ct = default)
        => (await GetPolicyAsync(bucketName, ct)).IsPublicRead;

    public string CreatePublicReadPolicyJson(string bucketName)
    {
        var policy = new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Sid = "PublicReadGetObject",
                    Effect = "Allow",
                    Principal = "*",
                    Action = "s3:GetObject",
                    Resource = $"arn:aws:s3:::{bucketName}/*"
                }
            }
        };

        return JsonSerializer.Serialize(policy, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public bool TryParseSupportedPolicy(string bucketName, string json, out string accessPolicy, out string? error)
    {
        accessPolicy = BucketAccessPolicies.Private;
        error = null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"Policy JSON is malformed: {ex.Message}";
            return false;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("Statement", out var statements))
            {
                error = "Policy must contain a Statement.";
                return false;
            }

            var statementList = statements.ValueKind == JsonValueKind.Array
                ? statements.EnumerateArray().ToList()
                : statements.ValueKind == JsonValueKind.Object
                    ? [statements]
                    : [];

            if (statementList.Count == 0)
            {
                error = "Policy must contain at least one statement.";
                return false;
            }

            var sawPublicRead = false;
            foreach (var statement in statementList)
            {
                if (!IsAllow(statement)
                    || !IsPublicPrincipal(statement)
                    || !ContainsValue(statement, "Action", "s3:GetObject")
                    || !ContainsValue(statement, "Resource", $"arn:aws:s3:::{bucketName}/*"))
                {
                    error = "Only public-read bucket policies are supported: Allow Principal \"*\" Action \"s3:GetObject\" Resource \"arn:aws:s3:::bucket/*\".";
                    return false;
                }

                sawPublicRead = true;
            }

            if (!sawPublicRead)
            {
                error = "Policy does not grant public object read access.";
                return false;
            }

            accessPolicy = BucketAccessPolicies.PublicRead;
            return true;
        }
    }

    private static bool IsAllow(JsonElement statement)
        => statement.TryGetProperty("Effect", out var effect)
           && effect.ValueKind == JsonValueKind.String
           && string.Equals(effect.GetString(), "Allow", StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicPrincipal(JsonElement statement)
    {
        if (!statement.TryGetProperty("Principal", out var principal))
            return false;

        if (principal.ValueKind == JsonValueKind.String)
            return principal.GetString() == "*";

        if (principal.ValueKind != JsonValueKind.Object)
            return false;

        return ContainsPrincipalValue(principal, "AWS")
               || ContainsPrincipalValue(principal, "CanonicalUser");
    }

    private static bool ContainsPrincipalValue(JsonElement principal, string propertyName)
        => principal.TryGetProperty(propertyName, out var value)
           && ElementContainsString(value, "*");

    private static bool ContainsValue(JsonElement statement, string propertyName, string expected)
        => statement.TryGetProperty(propertyName, out var value)
           && ElementContainsString(value, expected);

    private static bool ElementContainsString(JsonElement value, string expected)
    {
        if (value.ValueKind == JsonValueKind.String)
            return string.Equals(value.GetString(), expected, StringComparison.Ordinal);

        if (value.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), expected, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
