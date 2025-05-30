// <copyright file="SuggestNamespacesAnalyzer.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

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

        private static readonly DiagnosticDescriptor NamespaceNotFoundRule = new(
            id: NamespaceNotFoundDiagnosticId,
            title: Resources.NamespaceNotFoundTitle,
            messageFormat: Resources.NamespaceNotFoundMessageFormat,
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: Resources.NamespaceNotFoundDescription,
            helpLinkUri: HelpLinkUri,
            customTags: "AnalyzerReleaseTracking");

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(NamespaceNotFoundRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        /// <summary>
        /// Called at the start of compilation to register per-compilation actions.
        /// </summary>
        /// <param name="context">The compilation start analysis context.</param>
        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            // Collect all namespaces once per compilation
            ImmutableArray<string> allNamespaces = GetAllNamespaces(context.Compilation);

            // Register the syntax node action with the collected namespaces
            context.RegisterSyntaxNodeAction(
                syntaxContext => AnalyzeUsingDirective(syntaxContext, allNamespaces),
                SyntaxKind.UsingDirective);
        }

        /// <summary>
        /// Analyzes a using directive to check if the namespace exists and suggest alternatives if it doesn't.
        /// </summary>
        /// <param name="context">The syntax node analysis context.</param>
        /// <param name="allNamespaces">Pre-collected namespaces for this compilation.</param>
        private static void AnalyzeUsingDirective(SyntaxNodeAnalysisContext context, ImmutableArray<string> allNamespaces)
        {
            UsingDirectiveSyntax usingDirective = (UsingDirectiveSyntax)context.Node;
            if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                || usingDirective.Alias != null
                || usingDirective.Name is null)
            {
                return;
            }

            string namespaceName = usingDirective.Name.ToString();
            SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(usingDirective.Name, cancellationToken: context.CancellationToken);
            if (symbolInfo.Symbol != null)
            {
                return; // Namespace exists, nothing to do
            }

            IEnumerable<string> suggestions = GetSimilarNamespaces(allNamespaces, namespaceName);
            if (!suggestions.Any())
            {
                return;
            }

            string suggestionsText = "\n- " + string.Join("\n- ", suggestions);
            Diagnostic diagnostic = Diagnostic.Create(
                NamespaceNotFoundRule,
                usingDirective.Name.GetLocation(),
                namespaceName,
                suggestionsText);

            context.ReportDiagnostic(diagnostic);
        }

        /// <summary>
        /// Recursively collects namespace names from a namespace symbol hierarchy.
        /// </summary>
        /// <param name="ns">The namespace symbol to start collection from.</param>
        /// <param name="builder">The builder to add namespace names to.</param>
        private static void CollectNamespaces(INamespaceSymbol ns, ImmutableArray<string>.Builder builder)
        {
            string name = ns.ToDisplayString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                builder.Add(name);
            }

            foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
            {
                CollectNamespaces(child, builder);
            }
        }

        /// <summary>
        /// Gets all available namespaces from the compilation and its referenced assemblies.
        /// This method is called once per compilation to avoid stale cache issues.
        /// </summary>
        /// <param name="compilation">The compilation containing the namespaces.</param>
        /// <returns>An immutable array of namespace names.</returns>
        private static ImmutableArray<string> GetAllNamespaces(Compilation compilation)
        {
            ImmutableArray<string>.Builder builder = ImmutableArray.CreateBuilder<string>();
            CollectNamespaces(compilation.GlobalNamespace, builder);

            foreach (MetadataReference reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol asm)
                {
                    CollectNamespaces(asm.GlobalNamespace, builder);
                }
            }

            return builder.Distinct(StringComparer.Ordinal).ToImmutableArray();
        }

        /// <summary>
        /// Finds namespaces with names similar to the requested namespace name.
        /// </summary>
        /// <param name="allNamespaces">Pre-collected namespaces for this compilation.</param>
        /// <param name="namespaceName">The namespace name to find alternatives for.</param>
        /// <returns>A collection of similar namespace names.</returns>
        private static IEnumerable<string> GetSimilarNamespaces(ImmutableArray<string> allNamespaces, string namespaceName)
        {
            return Utils.StringSimilarity
                        .FindSimilarSymbols(namespaceName, allNamespaces.Select(static ns => (Key: ns, Value: ns)).ToList())
                        .Select(static r => r.Value);
        }
    }
}
