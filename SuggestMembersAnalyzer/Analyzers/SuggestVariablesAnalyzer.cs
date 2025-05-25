// <copyright file="SuggestVariablesAnalyzer.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using global::SuggestMembersAnalyzer.Utils;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// Analyzer that suggests closest matching locals / fields / properties / types
    /// when an identifier cannot be resolved by the compiler.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SuggestVariablesAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>Diagnostic ID for "variable not found".</summary>
        public const string VariableNotFoundDiagnosticId = "SMB002";

        private const string Category = "Usage";
        private const string HelpLinkUri = "https://github.com/skulidropek/DotnetSuggestMembersAnalyzer";

        /// <summary>
        /// Initial capacity for type collection to optimize memory allocation.
        /// </summary>
        private const int InitialTypeCollectionCapacity = 1024;

        /// <summary>
        /// Minimum similarity score threshold for type name matching.
        /// </summary>
        private const double MinimumTypeSimilarityThreshold = 0.7;

        // ───────────────────────────────────────────────────────────── Static data

        /// <summary>Fast O(1) lookup of C# keywords to ignore.</summary>
        private static readonly ImmutableHashSet<string> Keywords =
            new[]
            {
            // Full C# 12 keyword list incl. contextual
            "abstract", "add", "alias", "and", "as", "ascending", "async", "await", "base", "bool", "break", "byte",
            "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "descending",
            "do", "double", "dynamic", "else", "enum", "equals", "event", "explicit", "extern", "false", "file", "finally", "fixed",
            "float", "for", "foreach", "from", "get", "global", "goto", "group", "if", "implicit", "in", "init", "int", "interface",
            "internal", "into", "is", "join", "let", "lock", "long", "managed", "nameof", "namespace", "new", "not", "null", "object",
            "on", "operator", "or", "orderby", "out", "override", "params", "partial", "private", "protected", "public", "readonly",
            "record", "ref", "remove", "required", "return", "sbyte", "scoped", "sealed", "select", "set", "short", "sizeof",
            "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unmanaged", "unsafe", "ushort", "using", "value", "var", "virtual", "void", "volatile", "when", "where",
            "while", "with", "yield",
            }.ToImmutableHashSet(StringComparer.Ordinal);

        /// <summary>
        /// ───────────────────────────────────────────────────────────── Api-exposed descriptors.
        /// </summary>
        private static readonly DiagnosticDescriptor Rule = new(
            VariableNotFoundDiagnosticId,
            new LocalizableResourceString(nameof(Resources.VariableNotFoundTitle), Resources.ResourceManager, typeof(Resources)),
            new LocalizableResourceString(nameof(Resources.VariableNotFoundMessageFormat), Resources.ResourceManager, typeof(Resources)),
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: new LocalizableResourceString(nameof(Resources.VariableNotFoundDescription), Resources.ResourceManager, typeof(Resources)),
            helpLinkUri: HelpLinkUri);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        // ───────────────────────────────────────────────────────────── Initialization

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(compilationContext =>
            {
                Compilation compilation = compilationContext.Compilation;

                // Initialize helpers once per compilation
                SymbolFormatter.Initialize(compilation);

                // Collect type names for this compilation
                ImmutableArray<string> typeNames = CollectAllTypeNames(compilation);

                compilationContext.RegisterSyntaxNodeAction(
                    nodeCtx => AnalyzeIdentifier(nodeCtx, typeNames),
                    SyntaxKind.IdentifierName);
            });
        }

        // ───────────────────────────────────────────────────────────── Core analysis

        /// <summary>
        /// Analyse each <see cref="IdentifierNameSyntax"/> that failed binding and emit SMB002 suggestions.
        /// </summary>
        /// <param name="context">Analysis context.</param>
        /// <param name="allTypeNames">Collection of all available type names.</param>
        private static void AnalyzeIdentifier(
            SyntaxNodeAnalysisContext context,
            ImmutableArray<string> allTypeNames)
        {
            if (context.Node is not IdentifierNameSyntax id)
            {
                return;
            }

            string name = id.Identifier.ValueText;

            // Quick filters and validation
            if (ShouldSkipIdentifier(id, name, context.SemanticModel))
            {
                return;
            }

            // Check if we should analyze this identifier
            if (!ShouldAnalyzeForSuggestions(id, name, context.SemanticModel))
            {
                return;
            }

            // Generate and report suggestions
            List<object> suggestions = GatherSymbolSuggestions(id, name, context.SemanticModel, allTypeNames);
            if (suggestions.Count > 0)
            {
                ReportSuggestionDiagnostic(context, id, name, suggestions);
            }
        }

        /// <summary>
        /// Collects fully-qualified names of all types available to the compilation (source + metadata).
        /// </summary>
        /// <param name="compilation">The compilation to collect types from.</param>
        /// <returns>Array of fully-qualified type names.</returns>
        private static ImmutableArray<string> CollectAllTypeNames(Compilation compilation)
        {
            List<string> result = new(capacity: InitialTypeCollectionCapacity);

            void WalkNamespace(INamespaceSymbol ns)
            {
                // Enumerate direct types first
                foreach (INamedTypeSymbol type in ns.GetTypeMembers())
                {
                    AddTypeWithNested(type);
                }

                // Recurse into sub-namespaces
                foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
                {
                    WalkNamespace(child);
                }
            }

            void AddTypeWithNested(INamedTypeSymbol type)
            {
                result.Add(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty));
                foreach (INamedTypeSymbol nested in type.GetTypeMembers())
                {
                    AddTypeWithNested(nested);
                }
            }

            WalkNamespace(compilation.GlobalNamespace);

            // referenced assemblies
            foreach (IAssemblySymbol reference in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                WalkNamespace(reference.GlobalNamespace);
            }

            // De-duplicate without additional allocations
            return result.Distinct(StringComparer.Ordinal).ToImmutableArray();
        }

        /// <summary>
        /// Gathers symbol suggestions for an identifier.
        /// </summary>
        /// <param name="id">The identifier syntax.</param>
        /// <param name="name">The identifier name.</param>
        /// <param name="model">The semantic model.</param>
        /// <param name="allTypeNames">All available type names.</param>
        /// <returns>List of suggested symbols.</returns>
        private static List<object> GatherSymbolSuggestions(
            IdentifierNameSyntax id,
            string name,
            SemanticModel model,
            ImmutableArray<string> allTypeNames)
        {
            // Gather locals/fields/props + project type names
            IEnumerable<(string Key, object Value)> visibleSymbols = model.LookupSymbols(id.SpanStart)
                                      .Where(static s => s.Kind is SymbolKind.Local or SymbolKind.Parameter or
                                                  SymbolKind.Field or SymbolKind.Property or SymbolKind.Method)
                                      .Select(static s => (Key: s.Name, Value: (object)s));

            IEnumerable<(string Key, object Value)> typeEntries = allTypeNames.Select(static full =>
            {
                int last = full.LastIndexOf('.') + 1;
                string shortName = last > 0 ? full.Substring(last) : full;
                return (Key: shortName, Value: (object)full);
            });

            return [.. StringSimilarity
                .FindSimilarSymbols(name, visibleSymbols.Concat(typeEntries))
                .Select(static r => r.Value)
                .Where(static v => v != null),];
        }

        private static bool HasRelevantCompilerError(SemanticModel model, SyntaxNode id)
        {
            foreach (Diagnostic diag in model.GetDiagnostics(id.Span))
            {
                if (diag.Severity != DiagnosticSeverity.Error)
                {
                    continue;
                }

                if (diag.Id is "CS0103" or "CS0246" or "CS0234")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Fast check for XML doc trivia without walking entire trivia tree.
        /// </summary>
        /// <param name="node">Syntax node to check.</param>
        /// <returns>True if node is part of XML documentation.</returns>
        private static bool IsInXmlComment(SyntaxNode node)
        {
            return node.IsPartOfStructuredTrivia();
        }

        // ───────────────────────────────────────────────────────────── Helpers

        /// <summary>Return true if "nameof(x)" pattern surrounds <paramref name="id"/>.</summary>
        /// <param name="id">Identifier syntax to check.</param>
        /// <returns>True if identifier is within nameof() expression.</returns>
        private static bool IsNameOfOperand(IdentifierNameSyntax id)
        {
            // Check for nameof(IdentifierName) pattern
            if (id.Parent is InvocationExpressionSyntax { Expression: IdentifierNameSyntax inv }
                && inv.Identifier.ValueText.Equals("nameof", StringComparison.Ordinal))
            {
                return true;
            }

            // Check for parent argument of nameof
            return id.Parent is ArgumentSyntax { Parent: ArgumentListSyntax argList } &&
                argList.Parent is InvocationExpressionSyntax { Expression: IdentifierNameSyntax nameofExpr } &&
                nameofExpr.Identifier.ValueText.Equals("nameof", StringComparison.Ordinal);
        }

        /// <summary>Checks whether <paramref name="id"/> is exactly in a type-usage position.</summary>
        /// <param name="id">Identifier syntax to check.</param>
        /// <returns>True if identifier is in type position.</returns>
        private static bool IsTypePosition(IdentifierNameSyntax id)
        {
            return id.Parent switch
            {
                TypeSyntax => true,
                ObjectCreationExpressionSyntax => true,
                ParameterSyntax { Type: var t } => t == id,
                VariableDeclarationSyntax { Type: var t } => t == id,
                CastExpressionSyntax { Type: var t } => t == id,
                _ => false,
            };
        }

        /// <summary>
        /// Reports a suggestion diagnostic for an identifier.
        /// </summary>
        /// <param name="context">The analysis context.</param>
        /// <param name="id">The identifier syntax.</param>
        /// <param name="name">The identifier name.</param>
        /// <param name="suggestions">The list of suggestions.</param>
        private static void ReportSuggestionDiagnostic(
            SyntaxNodeAnalysisContext context,
            IdentifierNameSyntax id,
            string name,
            List<object> suggestions)
        {
            string entityKind = SymbolFormatter.GetEntityKind(suggestions[0]);
            string suggestionText = "\n- " + string.Join("\n- ", suggestions.Select(static s => s != null ? SymbolFormatter.FormatAny(s) : string.Empty));

            Diagnostic diagnostic = Diagnostic.Create(
                Rule,
                id.GetLocation(),
                entityKind,
                name,
                suggestionText);

            context.ReportDiagnostic(diagnostic);
        }

        /// <summary>
        /// Determines if an identifier should be analyzed for suggestions.
        /// </summary>
        /// <param name="id">The identifier syntax.</param>
        /// <param name="name">The identifier name.</param>
        /// <param name="model">The semantic model.</param>
        /// <returns>True if the identifier should be analyzed.</returns>
        private static bool ShouldAnalyzeForSuggestions(IdentifierNameSyntax id, string name, SemanticModel model)
        {
            // Check compiler diagnostics for «name/type not found»
            if (HasRelevantCompilerError(model, id) || IsTypePosition(id))
            {
                return true;
            }

            // Extra heuristic: ignore if identifier clearly unrelated to a known type
            return model.LookupSymbols(id.SpanStart)
                     .OfType<INamedTypeSymbol>()
                     .Any(sym => StringSimilarity.ComputeCompositeScore(name, sym.Name) > MinimumTypeSimilarityThreshold);
        }

        /// <summary>
        /// Determines if an identifier should be skipped from analysis.
        /// </summary>
        /// <param name="id">The identifier syntax.</param>
        /// <param name="name">The identifier name.</param>
        /// <param name="model">The semantic model.</param>
        /// <returns>True if the identifier should be skipped.</returns>
        private static bool ShouldSkipIdentifier(IdentifierNameSyntax id, string name, SemanticModel model)
        {
            // Quick filters — cheap checks first
            if (Keywords.Contains(name) ||
                id.Parent is NameColonSyntax || // named arguments
                id.Parent is VariableDeclaratorSyntax || // declarations
                id.Parent is MemberBindingExpressionSyntax || // ?.M
                (id.Parent is MemberAccessExpressionSyntax { Name: var n } && n == id) ||
                id.Ancestors().OfType<UsingDirectiveSyntax>().Any() ||
                IsInXmlComment(id) ||
                IsNameOfOperand(id))
            {
                return true;
            }

            SymbolInfo info = model.GetSymbolInfo(id);

            // Already resolved or only overload mismatch – nothing to do
            if (info.Symbol is not null ||
                info.CandidateReason == CandidateReason.OverloadResolutionFailure)
            {
                return true;
            }

            // Skip LINQ clauses / anonymous initializers
            return id.Ancestors().Any(static a => a is QueryExpressionSyntax or AnonymousObjectMemberDeclaratorSyntax);
        }
    }
}
