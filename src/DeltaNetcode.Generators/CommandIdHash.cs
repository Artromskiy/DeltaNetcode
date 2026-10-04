using System.Text;
using Microsoft.CodeAnalysis;

namespace Delta.Netcode.Generators;

internal static class CommandIdHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    internal static ulong Compute(INamedTypeSymbol type)
    {
        string metadataName = GetMetadataFullName(type);
        byte[] bytes = Encoding.UTF8.GetBytes(metadataName);
        ulong hash = OffsetBasis;
        for (int index = 0; index < bytes.Length; index++)
        {
            hash = unchecked((hash ^ bytes[index]) * Prime);
        }

        return hash;
    }

    internal static string GetMetadataFullName(INamedTypeSymbol type)
    {
        var namespaceParts = new Stack<string>();
        for (INamespaceSymbol? current = type.ContainingNamespace; current is not null && !current.IsGlobalNamespace; current = current.ContainingNamespace)
        {
            namespaceParts.Push(current.Name);
        }

        var typeParts = new Stack<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            typeParts.Push(current.MetadataName);
        }

        var name = new StringBuilder();
        while (namespaceParts.Count > 0)
        {
            if (name.Length > 0)
            {
                name.Append('.');
            }

            name.Append(namespaceParts.Pop());
        }

        if (name.Length > 0)
        {
            name.Append('.');
        }

        bool first = true;
        while (typeParts.Count > 0)
        {
            if (!first)
            {
                name.Append('+');
            }

            name.Append(typeParts.Pop());
            first = false;
        }

        return name.ToString();
    }
}
