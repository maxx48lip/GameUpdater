using System.Security.Cryptography;

namespace GameUpdater.Shared;

/// <summary>Streaming MD5 computation for files of any size.</summary>
public static class HashCalculator
{
    public const string AlgorithmName = "MD5";

    /// <summary>
    /// Computes the MD5 hash of a file, reading it in chunks so that large files
    /// do not have to fit into memory. Returns a lowercase hex string (32 chars).
    /// </summary>
    public static async Task<string> ComputeMd5Async(string path, CancellationToken cancellationToken = default)
    {
        MD5 md5;
        try
        {
            md5 = MD5.Create();
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "MD5 is unavailable on this system (FIPS policy?). GameUpdater requires MD5.", ex);
        }

        using (md5)
        {
            await using var stream = File.OpenRead(path);
            var hash = await md5.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
