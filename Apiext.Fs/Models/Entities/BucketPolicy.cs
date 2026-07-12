using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apiext.Fs.Models.Entities;

[Table("bucket_policies")]
public sealed class BucketPolicy
{
    [Key]
    [MaxLength(63)]
    [Column("bucket_name")]
    public string BucketName { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    [Column("access_policy")]
    public string AccessPolicy { get; set; } = BucketAccessPolicies.Private;
}

public static class BucketAccessPolicies
{
    public const string Private = "Private";
    public const string PublicRead = "PublicRead";
}
