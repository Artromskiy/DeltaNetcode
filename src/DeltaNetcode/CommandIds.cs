using System.Text;

namespace Delta.Netcode;

/// <summary>Creates stable command identifiers from CLR metadata names.</summary>
public static class CommandIds
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Hashes a metadata name with UTF-8 FNV-1a 64.</summary>
    /// <param name="metadataName">The command type's CLR metadata full name.</param>
    /// <returns>The 64-bit command identifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadataName"/> is <see langword="null"/>.</exception>
    public static ulong FromMetadataName(string metadataName)
    {
        Guard.ThrowIfNull(metadataName, nameof(metadataName));
        byte[] bytes = Encoding.UTF8.GetBytes(metadataName);
        ulong hash = FnvOffsetBasis;
        for (int index = 0; index < bytes.Length; index++)
        {
            hash = unchecked((hash ^ bytes[index]) * FnvPrime);
        }

        return hash;
    }
}
