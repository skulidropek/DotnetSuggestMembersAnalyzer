using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SuggestMembersAnalyzer.Utils;

namespace SuggestMembersAnalyzer
{
    /// <summary>
    /// SuggestVariablesAnalyzer.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SuggestVariablesAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for variable not found errors.
        /// </summary>
        public const string VariableNotFoundDiagnosticId = "SMB002";

        private const string Category = "Usage";
        private const string HelpLinkUri = "https://github.com/skulidropek/DotnetSuggestMembersAnalyzer";

        private static readonly LocalizableString VariableTitle = new LocalizableResourceString(
            nameof(Resources.VariableNotFoundTitle),
            Resources.ResourceManager,
            typeof(Resources));

        private static readonly LocalizableString VariableDescription = new LocalizableResourceString(
            nameof(Resources.VariableNotFoundDescription),
            Resources.ResourceManager,
            typeof(Resources));

        private static readonly DiagnosticDescriptor VariableNotFoundRule = new DiagnosticDescriptor(
            VariableNotFoundDiagnosticId,
            VariableTitle,
            new LocalizableResourceString(
                nameof(Resources.VariableNotFoundMessageFormat),
                Resources.ResourceManager,
                typeof(Resources)),
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: VariableDescription,
            helpLinkUri: HelpLinkUri,
            customTags: "AnalyzerReleaseTracking");

        /// <inheritdoc/>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            [VariableNotFoundRule];

        /// <inheritdoc/>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeIdentifier, SyntaxKind.IdentifierName);
        }

        // Cache of all type full names
        private static ImmutableArray<string> _allTypeNames;

        private static ImmutableArray<string> GetAllTypeNames(Compilation compilation)
        {
            if (!_allTypeNames.IsDefaultOrEmpty)
            {
                return _allTypeNames;
            }

            var builder = ImmutableArray.CreateBuilder<string>();

            void CollectNamespace(INamespaceSymbol ns)
            {
                foreach (var type in ns.GetTypeMembers())
                {
                    string full = type
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        .Replace("global::", "");
                    builder.Add(full);
                    foreach (var nested in type.GetTypeMembers())
                    {
                        string nestedFull = nested
                            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            .Replace("global::", "");
                        builder.Add(nestedFull);
                    }
                }
                foreach (var child in ns.GetNamespaceMembers())
                {
                    CollectNamespace(child);
                }
            }

            CollectNamespace(compilation.GlobalNamespace);
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol asm)
                {
                    CollectNamespace(asm.GlobalNamespace);
                }
            }

            _allTypeNames = [.. builder.Distinct()];
            return _allTypeNames;
        }

        private static void AnalyzeIdentifier(SyntaxNodeAnalysisContext context)
        {
            var id = (IdentifierNameSyntax)context.Node;
            string name = id.Identifier.ValueText;

            // Filters
            if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
            {
                return;
            }

            if (name == "nameof")
            {
                return;
            }

            if (id.Parent is VariableDeclaratorSyntax)
            {
                return;
            }

            if (id.Parent is ParameterSyntax)
            {
                return;
            }

            if (id.Ancestors().OfType<UsingDirectiveSyntax>().Any())
            {
                return;
            }

            if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id)
            {
                return;
            }

            if (id.Parent is MemberBindingExpressionSyntax)
            {
                return;
            }

            if (id.Ancestors()
                  .OfType<InvocationExpressionSyntax>()
                  .Any(inv => inv.Expression is IdentifierNameSyntax invId &&
                              invId.Identifier.ValueText == "nameof"))
            {
                return;
            }

            // Symbol check
            var model = context.SemanticModel;
            var info = model.GetSymbolInfo(id);
            if (info.Symbol != null || info.CandidateReason == CandidateReason.OverloadResolutionFailure)
            {
                return;
            }

            // Gather candidate symbols
            var symbols = model.LookupSymbols(id.SpanStart)
                .Where(s => s.Kind is SymbolKind.Local
                         or SymbolKind.Parameter
                         or SymbolKind.Field
                         or SymbolKind.Property
                         or SymbolKind.Method)
                .ToList();

            // Add all type names
            var typeNames = GetAllTypeNames(context.Compilation);

            // Prepare entries
            var symbolEntries = symbols.Select(s => (Key: s.Name, Value: (object)s));
            var typeEntries = typeNames.Select(fn =>
            {
                string[] parts = fn.Split('.');
                string shortName = parts[parts.Length - 1];
                return (Key: shortName, Value: (object)fn);
            });
            var entries = symbolEntries.Concat(typeEntries).ToList();

            // Find top-5 similar
            var similar = Utils.StringSimilarity
                .FindSimilarSymbols(name, entries)
                .Select(r => r.Value)
                .ToList();

            if (!similar.Any())
            {
                return;
            }

            // Determine entity kind based on first suggestion
            string entityKind = SymbolFormatter.GetEntityKind(similar[0]);

            // Format suggestions with labels
            var formatted = similar.Select(v =>
            {
                if (v is ISymbol sym)
                {
                    return SymbolFormatter.FormatSymbol(sym);
                }

                return "[Class] " + v.ToString();
            }).ToList();

            // Report diagnostic with kind, name, suggestions
            string suggestionText = "\n- " + string.Join("\n- ", formatted);
            var diagnostic = Diagnostic.Create(
                VariableNotFoundRule,
                id.GetLocation(),
                entityKind,
                name,
                suggestionText);

            context.ReportDiagnostic(diagnostic);
        }
    }
}
