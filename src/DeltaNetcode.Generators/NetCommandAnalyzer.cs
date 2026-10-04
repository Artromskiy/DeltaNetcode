using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Delta.Netcode.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NetCommandAnalyzer : DiagnosticAnalyzer
{
    public const string PinCommandId = "DNET0001";
    public const string InvalidCommandId = "DNET0002";

    internal static readonly DiagnosticDescriptor PinRule = new(
        PinCommandId,
        "Pin the net command ID",
        "Pin the current name-derived command ID 0x{0} explicitly",
        "DeltaNetcode",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An explicit ID keeps the command identity stable when its type name changes.");

    internal static readonly DiagnosticDescriptor InvalidRule = new(
        InvalidCommandId,
        "Net command ID cannot be zero",
        "Command ID 0 is reserved; use the name-derived ID 0x{0} or another non-zero ID",
        "DeltaNetcode",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [PinRule, InvalidRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.StructDeclaration, SyntaxKind.RecordDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not TypeDeclarationSyntax declaration
            || context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol type
            || type.TypeKind != TypeKind.Struct)
        {
            return;
        }

        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Delta.Netcode.NetCommandAttribute")
            {
                continue;
            }

            AttributeSyntax? syntax = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken) as AttributeSyntax;
            if (syntax is null)
            {
                continue;
            }

            ulong id = CommandIdHash.Compute(type);
            bool hasId = false;
            bool isZero = false;
            foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
            {
                if (argument.Key != "Id")
                {
                    continue;
                }

                hasId = true;
                isZero = argument.Value.Value is ulong value && value == 0;
                break;
            }

            if (hasId && !isZero)
            {
                continue;
            }

            string formattedId = id.ToString("X16", CultureInfo.InvariantCulture);
            ImmutableDictionary<string, string?> properties = ImmutableDictionary<string, string?>.Empty.Add("Id", formattedId);
            context.ReportDiagnostic(Diagnostic.Create(
                hasId ? InvalidRule : PinRule,
                syntax.Name.GetLocation(),
                properties,
                formattedId));
        }
    }
}
