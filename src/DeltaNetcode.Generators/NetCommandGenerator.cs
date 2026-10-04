using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Delta.Netcode.Generators;

[Generator]
public sealed class NetCommandGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor IdCollision = new(
        "DNET0003",
        "Net command ID collision",
        "Commands '{0}' and '{1}' resolve to the same ID 0x{2:X16}",
        "DeltaNetcode",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedType = new(
        "DNET0004",
        "Net command type cannot be generated",
        "Net command '{0}' must be a non-abstract, non-generic, non-ref-like class or struct accessible to generated code",
        "DeltaNetcode",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<CommandModel> commands = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Delta.Netcode.NetCommandAttribute",
            static (node, _) => node is StructDeclarationSyntax or ClassDeclarationSyntax or RecordDeclarationSyntax,
            static (syntax, _) => new CommandModel(
                (INamedTypeSymbol)syntax.TargetSymbol,
                syntax.TargetNode.GetLocation()));

        context.RegisterSourceOutput(commands.Collect(), static (productionContext, models) => Generate(productionContext, models));
    }

    private static void Generate(SourceProductionContext context, ImmutableArray<CommandModel> models)
    {
        var registrations = new List<(string TypeName, ulong Id, bool IsPredicted, Location Location)>();
        var ids = new Dictionary<ulong, string>();
        foreach (CommandModel model in models)
        {
            INamedTypeSymbol type = model.Type;
            if (type.TypeKind is not (TypeKind.Struct or TypeKind.Class)
                || type.IsRefLikeType
                || type.IsGenericType
                || type.IsStatic
                || type.IsAbstract
                || !IsAccessible(type))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedType, model.Location, type.ToDisplayString()));
                continue;
            }

            AttributeData? attribute = type.GetAttributes().FirstOrDefault(static item =>
                item.AttributeClass?.ToDisplayString() == "Delta.Netcode.NetCommandAttribute");
            ulong id = ReadId(attribute, out bool hasExplicitId);
            bool isPredicted = ReadPrediction(attribute);
            if (!hasExplicitId)
            {
                id = CommandIdHash.Compute(type);
            }

            if (id == 0)
            {
                continue;
            }

            string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (ids.TryGetValue(id, out string? previous))
            {
                context.ReportDiagnostic(Diagnostic.Create(IdCollision, model.Location, typeName, previous, id));
                continue;
            }

            ids.Add(id, typeName);
            registrations.Add((typeName, id, isPredicted, model.Location));
        }

        registrations.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.TypeName, right.TypeName));
        if (registrations.Count == 0)
        {
            return;
        }

        var source = new StringBuilder("namespace Delta.Netcode\n{\npublic static class GeneratedCommands\n{\n");
        source.AppendLine("    private static readonly global::Delta.Netcode.ICommandRegistration[] s_registrations = new global::Delta.Netcode.ICommandRegistration[]");
        source.AppendLine("    {");
        foreach ((string typeName, ulong id, bool isPredicted, _) in registrations)
        {
            string idText = id.ToString("X16", CultureInfo.InvariantCulture);
            source.Append("        new global::Delta.Netcode.CommandRegistration<")
                .Append(typeName).Append(">(0x").Append(idText).Append("UL, isPredicted: ")
                .Append(isPredicted ? "true" : "false").AppendLine("),");
        }

        source.AppendLine("    };");
        source.AppendLine("    public static global::System.ReadOnlySpan<global::Delta.Netcode.ICommandRegistration> Registrations => s_registrations;");
        source.AppendLine("    public static global::Delta.Netcode.ICommandRegistration GetRegistration<T>()");
        source.AppendLine("    {");
        source.AppendLine("        foreach (global::Delta.Netcode.ICommandRegistration registration in s_registrations)");
        source.AppendLine("        {");
        source.AppendLine("            if (registration.CommandType == typeof(T))");
        source.AppendLine("            {");
        source.AppendLine("                return registration;");
        source.AppendLine("            }");
        source.AppendLine("        }");
        source.AppendLine("        throw new global::System.Collections.Generic.KeyNotFoundException($\"No generated net command registration exists for '{typeof(T)}'.\");");
        source.AppendLine("    }");
        source.AppendLine("}");
        source.AppendLine("}");
        context.AddSource("GeneratedCommands.g.cs", source.ToString());
    }

    private static ulong ReadId(AttributeData? attribute, out bool hasId)
    {
        hasId = false;
        if (attribute is null)
        {
            return 0;
        }

        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == "Id" && argument.Value.Value is ulong value)
            {
                hasId = true;
                return value;
            }
        }

        return 0;
    }

    private static bool ReadPrediction(AttributeData? attribute)
    {
        if (attribute is null)
        {
            return false;
        }

        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == "Predicted" && argument.Value.Value is bool isPredicted)
            {
                return isPredicted;
            }
        }

        return false;
    }

    private static bool IsAccessible(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class CommandModel(INamedTypeSymbol type, Location location)
    {
        internal INamedTypeSymbol Type { get; } = type;

        internal Location Location { get; } = location;
    }
}
