using System.Text;

namespace Delta.Netcode;

public static class CommandIds
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

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
