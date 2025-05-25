// <copyright file="SuggestNamedArgumentsAnalyzer.cs" company="PlaceholderCompany">
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

        private static readonly LocalizableString Description = new LocalizableResourceString(
            nameof(Resources.NamedArgumentNotFoundDescription),
            Resources.ResourceManager,
            typeof(Resources));

        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(
            nameof(Resources.NamedArgumentNotFoundMessageFormat),
            Resources.ResourceManager,
            typeof(Resources));

        private static readonly DiagnosticDescriptor Rule = new(
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
        /// <param name="context">The syntax node analysis context containing the node to analyze.</param>
        private static void AnalyzeArgument(SyntaxNodeAnalysisContext context)
        {
            ArgumentSyntax arg = (ArgumentSyntax)context.Node;

            // Early validation for named arguments only
            if (!TryGetNamedArgumentInfo(arg, out string providedName, out ArgumentListSyntax? argumentList) || argumentList is null)
            {
                return;
            }

            // Get candidate methods and member type
            (IReadOnlyList<IMethodSymbol> candidateMethods, string memberType) = GetCandidateMethods(argumentList, context.SemanticModel);
            if (candidateMethods.Count == 0)
            {
                return;
            }

            // Check if provided name exists in any parameter
            if (IsValidParameterName(candidateMethods, providedName))
            {
                return;
            }

            // Build suggestions and report diagnostic
            string invokedName = GetInvokedName(candidateMethods[0]);
            string suggestionsText = BuildMethodSignatures(candidateMethods);

            ReportNamedArgumentDiagnostic(context, arg, memberType, providedName, invokedName, suggestionsText);
        }

        /// <summary>
        /// Builds formatted method signatures for diagnostic suggestions.
        /// </summary>
        /// <param name="candidateMethods">The candidate methods to format.</param>
        /// <returns>Formatted suggestions text with method signatures.</returns>
        private static string BuildMethodSignatures(IReadOnlyList<IMethodSymbol> candidateMethods)
        {
            List<string> signatures = [.. candidateMethods.Select(FormatMethodSignature)];
            return "\n- " + string.Join("\n- ", signatures);
        }

        /// <summary>
        /// Extracts method symbols from symbol info, handling both resolved and candidate symbols.
        /// </summary>
        /// <param name="info">The symbol info to extract methods from.</param>
        /// <returns>Collection of method symbols.</returns>
        private static IMethodSymbol[] ExtractMethodSymbols(SymbolInfo info)
        {
            return info.CandidateReason == CandidateReason.OverloadResolutionFailure
                ? [.. info.CandidateSymbols.OfType<IMethodSymbol>()]
                : info.Symbol is IMethodSymbol singleMethod ? [singleMethod] : [];
        }

        /// <summary>
        /// Formats a single method signature for display.
        /// </summary>
        /// <param name="method">The method to format.</param>
        /// <returns>Formatted method signature.</returns>
        private static string FormatMethodSignature(IMethodSymbol method)
        {
            string paramList = string.Join(
                ", ",
                method.Parameters.Select(static p =>
                    $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}"));

            if (method.MethodKind == MethodKind.Constructor)
            {
                return $"{method.ContainingType.Name}({paramList})";
            }

            // Handle generic methods
            string genericArgs = method is { IsGenericMethod: true } && method.TypeParameters.Length > 0
                ? $"<{string.Join(", ", method.TypeParameters.Select(static tp => tp.Name))}>"
                : string.Empty;

            return $"{method.Name}{genericArgs}({paramList})";
        }

        /// <summary>
        /// Gets candidate method symbols from invocation or object creation expressions.
        /// </summary>
        /// <param name="argumentList">The argument list containing the arguments.</param>
        /// <param name="semanticModel">The semantic model for symbol resolution.</param>
        /// <returns>A tuple containing candidate methods and member type description.</returns>
        private static (IReadOnlyList<IMethodSymbol> candidateMethods, string memberType) GetCandidateMethods(
            ArgumentListSyntax argumentList, SemanticModel semanticModel)
        {
            // 1) invocation: foo(bar: 1)
            if (argumentList.Parent is InvocationExpressionSyntax inv)
            {
                SymbolInfo info = semanticModel.GetSymbolInfo(inv.Expression);
                IMethodSymbol[] methods = ExtractMethodSymbols(info);
                return (methods, "Method");
            }

            // 2) object creation: new Ctor(name: "x")
            if (argumentList.Parent is ObjectCreationExpressionSyntax oc)
            {
                SymbolInfo info = semanticModel.GetSymbolInfo(oc);
                IMethodSymbol[] methods = ExtractMethodSymbols(info);
                return (methods, "Constructor");
            }

            return (Array.Empty<IMethodSymbol>(), string.Empty);
        }

        /// <summary>
        /// Gets the invoked name for display in diagnostic messages.
        /// </summary>
        /// <param name="method">The method symbol to extract name from.</param>
        /// <returns>The method or type name for display.</returns>
        private static string GetInvokedName(IMethodSymbol method)
        {
            return method.MethodKind == MethodKind.Constructor
                ? method.ContainingType.Name
                : method.Name;
        }

        /// <summary>
        /// Checks if the provided parameter name exists in any of the candidate methods.
        /// </summary>
        /// <param name="candidateMethods">The candidate methods to check.</param>
        /// <param name="providedName">The provided parameter name.</param>
        /// <returns>True if the parameter name exists in any method.</returns>
        private static bool IsValidParameterName(IReadOnlyList<IMethodSymbol> candidateMethods, string providedName)
        {
            List<string> allParams = [.. candidateMethods
                .SelectMany(static m => m.Parameters.Select(static p => p.Name))
                .Distinct(StringComparer.Ordinal),];

            return allParams.Contains(providedName, StringComparer.Ordinal);
        }

        /// <summary>
        /// Reports a diagnostic for an invalid named argument.
        /// </summary>
        /// <param name="context">The analysis context.</param>
        /// <param name="arg">The argument syntax with the error.</param>
        /// <param name="memberType">The type of member (Method or Constructor).</param>
        /// <param name="providedName">The provided parameter name.</param>
        /// <param name="invokedName">The invoked method/constructor name.</param>
        /// <param name="suggestionsText">The formatted suggestions text.</param>
        private static void ReportNamedArgumentDiagnostic(
            SyntaxNodeAnalysisContext context,
            ArgumentSyntax arg,
            string memberType,
            string providedName,
            string invokedName,
            string suggestionsText)
        {
            Diagnostic diagnostic = Diagnostic.Create(
                Rule,
                arg.NameColon?.Name.GetLocation() ?? arg.GetLocation(),
                memberType,          // element type (Method or Constructor)
                providedName,        // parameter name
                invokedName,         // method/constructor name
                suggestionsText);    // available signatures

            context.ReportDiagnostic(diagnostic);
        }

        /// <summary>
        /// Validates if the argument is a named argument and extracts relevant information.
        /// </summary>
        /// <param name="arg">The argument syntax to validate.</param>
        /// <param name="providedName">The provided parameter name.</param>
        /// <param name="argumentList">The containing argument list.</param>
        /// <returns>True if this is a named argument that should be analyzed.</returns>
        private static bool TryGetNamedArgumentInfo(ArgumentSyntax arg, out string providedName, out ArgumentListSyntax? argumentList)
        {
            providedName = string.Empty;
            argumentList = null;

            if (arg.NameColon is null)
            {
                return false; // only named arguments
            }

            providedName = arg.NameColon.Name.Identifier.Text;

            if (arg.Parent is ArgumentListSyntax argList)
            {
                argumentList = argList;
                return true;
            }

            return false;
        }
    }
}
