using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Apiext.Fs.Models.Entities;

/// <summary>
/// Cached object metadata used by ListObjects / GetObject so that ETags are
/// consistent across both APIs and file content does not have to be re-hashed
/// on every request.
///
/// The file system stays the source of truth: an entry is only trusted when
/// <see cref="Size"/> and <see cref="LastWriteUtcTicks"/> still match the file.
/// </summary>
[Table("object_index")]
public sealed class ObjectIndexEntry
{
    [Column("bucket")]
    [MaxLength(255)]
    public string Bucket { get; set; } = string.Empty;

    [Column("key")]
    [MaxLength(1024)]
    public string Key { get; set; } = string.Empty;

    [Column("size")]
    public long Size { get; set; }

    [Column("last_write_utc_ticks")]
    public long LastWriteUtcTicks { get; set; }

    [Column("etag")]
    [MaxLength(128)]
    public string ETag { get; set; } = string.Empty;

    [Column("content_type")]
    [MaxLength(255)]
    public string? ContentType { get; set; }

    [Column("last_modified")]
    [MaxLength(64)]
    public string? LastModified { get; set; }
}
