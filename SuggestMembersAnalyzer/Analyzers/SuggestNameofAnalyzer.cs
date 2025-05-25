// <copyright file="SuggestNameofAnalyzer.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Analyzers
{
    using System;
    using System.Collections.Immutable;
    using System.Linq;
    using global::SuggestMembersAnalyzer.Utils;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// Analyzer for detecting and validating nameof() expressions in code.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SuggestNameofAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>Diagnostic ID for suggesting to use nameof instead of string literals.</summary>
        public const string UseNameofInsteadOfStringDiagnosticId = "SMB005";

        private const string Category = "Usage";
        private const string HelpLinkUri = "https://github.com/skulidropek/DotnetSuggestMembersAnalyzer";

        private static readonly DiagnosticDescriptor UseNameofRule = new(
            UseNameofInsteadOfStringDiagnosticId,
            new LocalizableResourceString(nameof(Resources.UseNameofInsteadOfStringTitle), Resources.ResourceManager, typeof(Resources)),
            new LocalizableResourceString(nameof(Resources.UseNameofInsteadOfStringMessageFormat), Resources.ResourceManager, typeof(Resources)),
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: new LocalizableResourceString(nameof(Resources.UseNameofInsteadOfStringDescription), Resources.ResourceManager, typeof(Resources)),
            helpLinkUri: HelpLinkUri);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(UseNameofRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            // Register for invocation expressions (to catch nameof calls)
            context.RegisterSyntaxNodeAction(
                AnalyzeNameofInvocation,
                SyntaxKind.InvocationExpression);
        }

        /// <summary>
        /// Analyzes invocations of nameof operator and validates that the arguments exist.
        /// </summary>
        /// <param name="context">Analysis context.</param>
        private static void AnalyzeNameofInvocation(SyntaxNodeAnalysisContext context)
        {
            InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;

            // Check if this is a nameof() call
            if (invocation.Expression is not IdentifierNameSyntax identifier ||
                !identifier.Identifier.ValueText.Equals("nameof", StringComparison.Ordinal))
            {
                return;
            }

            // Verify the argument exists
            if (!invocation.ArgumentList.Arguments.Any())
            {
                // nameof() without arguments is a compiler error already
                return;
            }

            // Process all expressions in nameof() arguments
            foreach (ExpressionSyntax? expression in invocation.ArgumentList.Arguments.Select(static arg => arg.Expression))
            {
                SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(expression, cancellationToken: context.CancellationToken);

                // If the argument doesn't resolve to a symbol and there are no candidates,
                // it's likely an invalid reference
                if (symbolInfo is { Symbol: null } &&
                    !symbolInfo.CandidateSymbols.Any() &&
                    symbolInfo.CandidateReason != CandidateReason.OverloadResolutionFailure)
                {
                    // Check if it's a qualified name that partially resolves
                    bool partiallyResolved = false;
                    if (expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        SymbolInfo leftInfo = context.SemanticModel.GetSymbolInfo(memberAccess.Expression, cancellationToken: context.CancellationToken);

                        // If the left part resolves, it might just be that the right part is invalid
                        partiallyResolved = leftInfo.Symbol != null;
                    }

                    if (!partiallyResolved)
                    {
                        // Get suggestions for similar symbols
                        string suggestions = FindSimilarSymbols(context, expression);

                        Diagnostic diagnostic = Diagnostic.Create(
                            UseNameofRule,
                            expression.GetLocation(),
                            expression.ToString(),
                            suggestions);

                        context.ReportDiagnostic(diagnostic);
                    }
                }
            }
        }

        /// <summary>
        /// Finds symbols similar to the given expression to suggest as alternatives.
        /// </summary>
        /// <param name="context">Analysis context.</param>
        /// <param name="expression">Expression to find similar symbols for.</param>
        /// <returns>Formatted string of suggestions.</returns>
        private static string FindSimilarSymbols(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
        {
            // Lookup visible symbols at this location
            SemanticModel model = context.SemanticModel;
            System.Collections.Generic.List<ISymbol> symbols = [.. model.LookupSymbols(expression.SpanStart)
                               .Where(static s => s.Kind is SymbolKind.Field or SymbolKind.Property
                                        or SymbolKind.Method or SymbolKind.Local
                                        or SymbolKind.Parameter),];

            // Find the string representation of the expression
            string expressionText = expression.ToString();

            // Find similar symbols using string similarity
            System.Collections.Generic.IEnumerable<(string Key, object Value)> entries =
                symbols.Select(static s => (Key: s.Name, Value: (object)s));
            System.Collections.Generic.List<(string Name, object Value, double Score)> similar =
                [.. StringSimilarity.FindSimilarSymbols(expressionText, entries)
                    .Where(static result => result.Value != null),];

            return similar.Count == 0
                ? " No suggestions available"
                : "\n- " + string.Join("\n- ", similar.Select(static s => SymbolFormatter.FormatAny(s.Value)));
        }
    }
}