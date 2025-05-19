using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SuggestMembersAnalyzer
{
    /// <summary>
    /// Analyzer that reports use of non‐existent named arguments and suggests correct parameter names.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SuggestNamedArgumentsAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for named argument not found errors.
        /// </summary>
        public const string DiagnosticId = "SMB004";
        private const string Category = "Usage";
        private const string HelpLinkUri = "https://github.com/skulidropek/DotnetSuggestMembersAnalyzer";

        private static readonly LocalizableString Title = new LocalizableResourceString(
            nameof(Resources.NamedArgumentNotFoundTitle),
            Resources.ResourceManager,
            typeof(Resources));
        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(
            nameof(Resources.NamedArgumentNotFoundMessageFormat),
            Resources.ResourceManager,
            typeof(Resources));
        private static readonly LocalizableString Description = new LocalizableResourceString(
            nameof(Resources.NamedArgumentNotFoundDescription),
            Resources.ResourceManager,
            typeof(Resources));

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: Description,
            helpLinkUri: HelpLinkUri,
            customTags: "AnalyzerReleaseTracking");

        /// <summary>
        /// Gets the supported diagnostics.
        /// </summary>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(Rule);

        /// <summary>
        /// Initializes the analyzer.
        /// </summary>
        /// <param name="context">The analysis context.</param>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeArgument, SyntaxKind.Argument);
        }

        /// <summary>
        /// Analyzes an argument node to detect incorrect named arguments and suggest alternatives.
        /// </summary>
        /// <param name="context">The syntax node analysis context containing the node to analyze</param>
        private static void AnalyzeArgument(SyntaxNodeAnalysisContext context)
        {
            var arg = (ArgumentSyntax)context.Node;
            if (arg.NameColon == null)
            {
                return; // only named arguments
            }

            string providedName = arg.NameColon.Name.Identifier.Text;
            if (!(arg.Parent is ArgumentListSyntax argumentList))
            {
                return;
            }

            // collect candidate overloads
            IReadOnlyList<IMethodSymbol> candidateMethods;
            string invokedName = "";
            string memberType = "";

            // 1) invocation: foo(bar: 1)
            if (argumentList.Parent is InvocationExpressionSyntax inv)
            {
                var info = context.SemanticModel.GetSymbolInfo(inv.Expression);
                
                // Simplify nested ternary
                if (info.CandidateReason == CandidateReason.OverloadResolutionFailure)
                {
                    candidateMethods = info.CandidateSymbols.OfType<IMethodSymbol>().ToList();
                }
                else if (info.Symbol is IMethodSymbol single)
                {
                    candidateMethods = new[] { single };
                }
                else
                {
                    candidateMethods = Array.Empty<IMethodSymbol>();
                }
                
                memberType = "Method";
            }
            // 2) object creation: new Ctor(name: "x")
            else if (argumentList.Parent is ObjectCreationExpressionSyntax oc)
            {
                var info = context.SemanticModel.GetSymbolInfo(oc);
                
                // Simplify nested ternary
                if (info.CandidateReason == CandidateReason.OverloadResolutionFailure)
                {
                    candidateMethods = info.CandidateSymbols.OfType<IMethodSymbol>().ToList();
                }
                else if (info.Symbol is IMethodSymbol singleCtor)
                {
                    candidateMethods = new[] { singleCtor };
                }
                else
                {
                    candidateMethods = Array.Empty<IMethodSymbol>();
                }
                
                memberType = "Constructor";
            }
            else
            {
                return;
            }

            if (candidateMethods.Count == 0)
            {
                return;
            }

            // name for the message
            invokedName = candidateMethods[0].MethodKind == MethodKind.Constructor
                ? candidateMethods[0].ContainingType.Name
                : candidateMethods[0].Name;

            // combine all parameters from all overloads
            var allParams = candidateMethods
                .SelectMany(m => m.Parameters.Select(p => p.Name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // if our provided name is among them — exit
            if (allParams.Contains(providedName, StringComparer.Ordinal))
            {
                return;
            }

            // build a list of signatures for all candidates
            var signatures = candidateMethods.Select(m =>
            {
                // parameters
                string paramList = string.Join(", ",
                    m.Parameters.Select(p =>
                        $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}"));
                if (m.MethodKind == MethodKind.Constructor)
                {
                    return $"{m.ContainingType.Name}({paramList})";
                }
                else
                {
                    // generic
                    string genericArgs = m.IsGenericMethod && m.TypeParameters.Length > 0
                        ? $"<{string.Join(", ", m.TypeParameters.Select(tp => tp.Name))}>"
                        : "";
                    return $"{m.Name}{genericArgs}({paramList})";
                }
            }).ToList();

            // format as "\n- Sig(...)"
            string suggestionsText = "\n- " + string.Join("\n- ", signatures);

            // create diagnostic
            var diag = Diagnostic.Create(
                Rule,
                arg.NameColon.Name.GetLocation(),
                memberType,          // element type (Method or Constructor)
                providedName,        // parameter name
                invokedName,         // method/constructor name
                suggestionsText);    // available signatures

            context.ReportDiagnostic(diag);
        }
    }
}
