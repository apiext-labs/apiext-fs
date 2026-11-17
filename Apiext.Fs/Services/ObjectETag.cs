using System.Security.Cryptography;

namespace Apiext.Fs.Services;

/// <summary>
/// Single source of truth for object ETag formatting. ListObjects and
/// GetObject must produce identical values for the same file.
/// </summary>
public static class ObjectETag
{
    private const int StreamCopyBufferSize = 81_920; // 80 KB

    /// <summary>Computes the content MD5 of a file and returns it as a quoted ETag.</summary>
    public static string ComputeFromFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, StreamCopyBufferSize, useAsync: false);
        return ComputeFromStream(stream);
    }

    /// <summary>Computes the content MD5 of a stream and returns it as a quoted ETag.</summary>
    public static string ComputeFromStream(Stream stream)
    {
        var hash = MD5.HashData(stream);
        return $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
    }
}
