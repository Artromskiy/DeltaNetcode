using System.Collections.Immutable;
using System.Globalization;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Delta.Netcode.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NetCommandIdCodeFixProvider)), Shared]
public sealed class NetCommandIdCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ["DNET0001", "DNET0002"];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Diagnostic diagnostic = context.Diagnostics[0];
        if (!diagnostic.Properties.TryGetValue("Id", out string? idText)
            || !ulong.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id))
        {
            return;
        }

        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        AttributeSyntax? attribute = root?.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<AttributeSyntax>();
        if (attribute is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Pin command ID",
                cancellationToken => AddIdAsync(context.Document, attribute, id, cancellationToken),
                equivalenceKey: "PinCommandId"),
            diagnostic);
    }

    private static async Task<Document> AddIdAsync(
        Document document,
        AttributeSyntax attribute,
        ulong id,
        CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        AttributeArgumentListSyntax arguments = attribute.ArgumentList ?? SyntaxFactory.AttributeArgumentList();
        SeparatedSyntaxList<AttributeArgumentSyntax> retained = SyntaxFactory.SeparatedList(
            arguments.Arguments.Where(static argument => argument.NameEquals?.Name.Identifier.ValueText != "Id"));
        string idValue = "0x" + id.ToString("X16", CultureInfo.InvariantCulture) + "UL";
        AttributeArgumentSyntax explicitId = SyntaxFactory.AttributeArgument(
            SyntaxFactory.NameEquals("Id"),
            nameColon: null,
            SyntaxFactory.ParseExpression(idValue));
        AttributeSyntax updated = attribute.WithArgumentList(arguments.WithArguments(retained.Add(explicitId)));
        return document.WithSyntaxRoot(root.ReplaceNode(attribute, updated));
    }
}
