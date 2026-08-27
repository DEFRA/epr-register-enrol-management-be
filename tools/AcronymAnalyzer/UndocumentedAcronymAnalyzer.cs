using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace AcronymAnalyzer;

// Guards against "OJ" and "CM" creeping back into the codebase after the
// chore/rename-oj-cm-acronyms cleanup: both were undocumented two-letter
// acronyms ("Operator Journey" and "Case Management") that made the code
// harder to follow for anyone not already carrying that tribal knowledge.
// Runs as part of the normal build (dotnet build / dotnet test), the same
// gate every other compiler diagnostic goes through — no separate CI step.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UndocumentedAcronymAnalyzer : DiagnosticAnalyzer
{
    private const string DiagnosticId = "ACR001";

    // Catches bare "OJ"/"CM" in comments, string literals and standalone
    // identifiers (anywhere the token is bounded by non-word characters).
    private static readonly Regex s_wordPattern = new(
        @"\b(OJ|CM)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // Splits an identifier into its camelCase/PascalCase segments, so a
    // compound identifier like "TryMapCmKeyToSection" is checked segment by
    // segment ("Try", "Map", "Cm", "Key", "To", "Section") — the word-boundary
    // pattern above alone can't see "Cm" there, since nothing but a case
    // change separates it from its neighbours.
    private static readonly Regex s_segmentPattern = new(
        @"[A-Z]+(?=[A-Z][a-z]|$|[^A-Za-z])|[A-Z]?[a-z]+",
        RegexOptions.Compiled
    );

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        title: "Undocumented acronym",
        messageFormat:
            "Avoid the acronym '{0}' — spell out '{1}' instead (see chore/rename-oj-cm-acronyms)",
        category: "Naming",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(s_rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(AnalyzeText);
        context.RegisterSyntaxTreeAction(AnalyzeIdentifiers);
    }

    private static void AnalyzeText(SyntaxTreeAnalysisContext context)
    {
        var text = context.Tree.GetText(context.CancellationToken);
        var content = text.ToString();

        foreach (Match match in s_wordPattern.Matches(content))
        {
            Report(context, match.Index, match.Length, match.Value);
        }
    }

    private static void AnalyzeIdentifiers(SyntaxTreeAnalysisContext context)
    {
        var root = context.Tree.GetRoot(context.CancellationToken);

        foreach (var token in root.DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken))
            {
                continue;
            }

            var name = token.Text;

            foreach (Match segment in s_segmentPattern.Matches(name))
            {
                if (
                    segment.Value.Equals("OJ", StringComparison.OrdinalIgnoreCase)
                    || segment.Value.Equals("CM", StringComparison.OrdinalIgnoreCase)
                )
                {
                    Report(context, token.SpanStart + segment.Index, segment.Length, segment.Value);
                }
            }
        }
    }

    private static void Report(SyntaxTreeAnalysisContext context, int start, int length, string matchedText)
    {
        var term = matchedText.ToUpperInvariant();
        var full = term == "OJ" ? "Registration & Accreditation service" : "Case Management service";
        var location = Location.Create(context.Tree, new TextSpan(start, length));
        context.ReportDiagnostic(Diagnostic.Create(s_rule, location, term, full));
    }
}
