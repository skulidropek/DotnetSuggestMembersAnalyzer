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
        private static void AnalyzeNameofInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            
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
            foreach (var expression in invocation.ArgumentList.Arguments.Select(arg => arg.Expression))
            {
                var symbolInfo = context.SemanticModel.GetSymbolInfo(expression);

                // If the argument doesn't resolve to a symbol and there are no candidates,
                // it's likely an invalid reference
                if (symbolInfo.Symbol == null && 
                    !symbolInfo.CandidateSymbols.Any() && 
                    symbolInfo.CandidateReason != CandidateReason.OverloadResolutionFailure)
                {
                    // Check if it's a qualified name that partially resolves
                    bool partiallyResolved = false;
                    if (expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        var leftInfo = context.SemanticModel.GetSymbolInfo(memberAccess.Expression);
                        // If the left part resolves, it might just be that the right part is invalid
                        partiallyResolved = leftInfo.Symbol != null;
                    }

                    if (!partiallyResolved)
                    {
                        // Get suggestions for similar symbols
                        var suggestions = FindSimilarSymbols(context, expression);
                        
                        var diagnostic = Diagnostic.Create(
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
        private static string FindSimilarSymbols(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
        {
            // Lookup visible symbols at this location
            var model = context.SemanticModel;
            var symbols = model.LookupSymbols(expression.SpanStart)
                               .Where(s => s.Kind is SymbolKind.Field or SymbolKind.Property 
                                        or SymbolKind.Method or SymbolKind.Local 
                                        or SymbolKind.Parameter)
                               .ToList();

            // Find the string representation of the expression
            string expressionText = expression.ToString();
            
            // Find similar symbols using string similarity
            var entries = symbols.Select(s => (Key: s.Name, Value: (object)s));
            var similar = StringSimilarity.FindSimilarSymbols(expressionText, entries)
                                         .Where(result => result.Value != null)
                                         .ToList();
            
            if (similar.Count == 0)
            {
                return " No suggestions available";
            }
            
            return "\n- " + string.Join("\n- ", similar.Select(s => SymbolFormatter.FormatAny(s.Value)));
        }
    }
} 