using System;
using System.Collections.Generic;
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
    /// Analyzer that suggests correct namespaces for misspelled using-directives.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SuggestNamespacesAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for namespace not found errors.
        /// </summary>
        public const string NamespaceNotFoundDiagnosticId = "SMB003";

        private const string Category = "Usage";
        private const string HelpLinkUri = "https://github.com/skulidropek/DotnetSuggestMembersAnalyzer";

        private static readonly DiagnosticDescriptor NamespaceNotFoundRule = new DiagnosticDescriptor(
            /* id */            NamespaceNotFoundDiagnosticId,
            /* title */         Resources.NamespaceNotFoundTitle,
            /* messageFormat */ Resources.NamespaceNotFoundMessageFormat,
            /* category */      Category,
            /* severity */      DiagnosticSeverity.Error,
            /* isEnabled */     true,
            /* description */   Resources.NamespaceNotFoundDescription,
            /* helpLink */      HelpLinkUri,
            /* customTags */    "AnalyzerReleaseTracking");

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(NamespaceNotFoundRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeUsingDirective, SyntaxKind.UsingDirective);
        }

        private static void AnalyzeUsingDirective(SyntaxNodeAnalysisContext context)
        {
            var usingDirective = (UsingDirectiveSyntax)context.Node;
            if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                || usingDirective.Alias != null
                || usingDirective.Name == null)
            {
                return;
            }

            var namespaceName = usingDirective.Name.ToString();
            var symbolInfo = context.SemanticModel.GetSymbolInfo(usingDirective.Name);
            if (symbolInfo.Symbol != null)
            {
                return; // Namespace exists, nothing to do
            }

            var suggestions = GetSimilarNamespaces(context.Compilation, namespaceName);
            if (!suggestions.Any())
            {
                return;
            }

            var suggestionsText = "\n- " + string.Join("\n- ", suggestions);
            var diagnostic = Diagnostic.Create(
                NamespaceNotFoundRule,
                usingDirective.Name.GetLocation(),
                namespaceName,
                suggestionsText);

            context.ReportDiagnostic(diagnostic);
        }

        // Cache of all namespaces
        private static ImmutableArray<string> _allNamespaces;

        private static ImmutableArray<string> GetAllNamespaces(Compilation compilation)
        {
            if (!_allNamespaces.IsDefaultOrEmpty)
            {
                return _allNamespaces;
            }

            var builder = ImmutableArray.CreateBuilder<string>();
            CollectNamespaces(compilation.GlobalNamespace, builder);

            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol asm)
                {
                    CollectNamespaces(asm.GlobalNamespace, builder);
                }
            }

            _allNamespaces = builder.Distinct().ToImmutableArray();
            return _allNamespaces;
        }

        private static void CollectNamespaces(INamespaceSymbol ns, ImmutableArray<string>.Builder builder)
        {
            var name = ns.ToDisplayString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                builder.Add(name);
            }

            foreach (var child in ns.GetNamespaceMembers())
            {
                CollectNamespaces(child, builder);
            }
        }

        private static IEnumerable<string> GetSimilarNamespaces(Compilation compilation, string namespaceName)
        {
            var allNs = GetAllNamespaces(compilation);
            return Utils.StringSimilarity
                        .FindSimilarSymbols(namespaceName, allNs.Select(ns => (Key: ns, Value: ns)).ToList())
                        .Select(r => r.Value);
        }
    }
}
