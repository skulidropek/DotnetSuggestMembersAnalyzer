// File: SuggestMembersAnalyzer/SuggestVariablesAnalyzer.cs
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
    /// Analyzer that suggests possible local variables, fields, properties, or types
    /// when referencing non-existent identifiers.
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

        private static readonly LocalizableString Title = new LocalizableResourceString(
            nameof(Resources.VariableNotFoundTitle),
            Resources.ResourceManager,
            typeof(Resources));
        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(
            nameof(Resources.VariableNotFoundMessageFormat),
            Resources.ResourceManager,
            typeof(Resources));
        private static readonly LocalizableString Description = new LocalizableResourceString(
            nameof(Resources.VariableNotFoundDescription),
            Resources.ResourceManager,
            typeof(Resources));

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            VariableNotFoundDiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: Description,
            helpLinkUri: HelpLinkUri);

        /// <summary>
        /// Gets the supported diagnostics.
        /// </summary>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        /// <summary>
        /// Initializes the analyzer.
        /// </summary>
        /// <param name="context">The analysis context.</param>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(compilationContext =>
            {
                var compilation = compilationContext.Compilation;
                
                // Initialize SymbolFormatter with the current compilation
                SymbolFormatter.Initialize(compilation);
                
                var allTypeNames = CollectAllTypeNames(compilation);

                compilationContext.RegisterSyntaxNodeAction(
                    ctx => AnalyzeIdentifier(ctx, allTypeNames),
                    SyntaxKind.IdentifierName);
            });
        }

        /// <summary>
        /// Collects all type names from the compilation and its referenced assemblies.
        /// </summary>
        /// <param name="compilation">The compilation containing the types</param>
        /// <returns>An immutable array of fully qualified type names</returns>
        private static ImmutableArray<string> CollectAllTypeNames(Compilation compilation)
        {
            var builder = ImmutableArray.CreateBuilder<string>();

            // Local function to recursively walk namespace hierarchies
            void WalkNamespace(INamespaceSymbol ns)
            {
                foreach (var type in ns.GetTypeMembers())
                {
                    var full = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                   .Replace("global::", "");
                    builder.Add(full);
                    foreach (var nested in type.GetTypeMembers())
                    {
                        builder.Add(
                            nested.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                  .Replace("global::", ""));
                    }
                }

                foreach (var child in ns.GetNamespaceMembers())
                {
                    WalkNamespace(child);
                }
            }

            WalkNamespace(compilation.GlobalNamespace);

            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol asm)
                {
                    WalkNamespace(asm.GlobalNamespace);
                }
            }

            return builder.Distinct().ToImmutableArray();
        }

        /// <summary>
        /// Analyzes an identifier node to detect undefined variables and suggest alternatives.
        /// </summary>
        /// <param name="context">The syntax node analysis context</param>
        /// <param name="allTypeNames">Collection of all available type names for suggestions</param>
        private static void AnalyzeIdentifier(
            SyntaxNodeAnalysisContext context,
            ImmutableArray<string> allTypeNames)
        {
            var id = (IdentifierNameSyntax)context.Node;
            var name = id.Identifier.ValueText;

            // named arguments, declarations, LINQ, member access, keywords → skip
            if (id.Parent is NameColonSyntax ||
                (new[] {
                    "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue",
                    "decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally",
                    "fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long",
                    "namespace","new","null","object","operator","out","override","params","private","protected","public",
                    "readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch",
                    "this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void",
                    "volatile","while","add","alias","and","ascending","async","await","by","descending","dynamic","equals","file",
                    "from","get","global","group","init","into","join","let","managed","nameof","not","on","or","orderby","partial",
                    "record","remove","required","scoped","select","set","unmanaged","value","var","when","where","with","yield"}
                    ).Contains(name) ||
                id.Parent is VariableDeclaratorSyntax ||
                id.Parent is ParameterSyntax ||
                id.Ancestors().OfType<UsingDirectiveSyntax>().Any() ||
                (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id) ||
                id.Parent is MemberBindingExpressionSyntax ||
                id.Ancestors().OfType<InvocationExpressionSyntax>()
                  .Any(inv => inv.Expression is IdentifierNameSyntax invId &&
                              invId.Identifier.ValueText == "nameof"))
            {
                return;
            }

            var model = context.SemanticModel;
            var info  = model.GetSymbolInfo(id);
            if (info.Symbol != null ||
                info.CandidateReason == CandidateReason.OverloadResolutionFailure)
            {
                return;
            }

            if (id.Parent is VariableDeclarationSyntax varDecl && varDecl.Type == id)
            {
                return;
            }

            if (id.Ancestors().Any(n => n is QueryExpressionSyntax || n is AnonymousObjectMemberDeclaratorSyntax))
            {
                return;
            }

            // collect available local symbols
            var locals = model.LookupSymbols(id.SpanStart)
                              .Where(s => s.Kind is SymbolKind.Local
                                       or SymbolKind.Parameter
                                       or SymbolKind.Field
                                       or SymbolKind.Property
                                       or SymbolKind.Method);

            // + all types from project and references
            var typeEntries = allTypeNames.Select(fn =>
            {
                var parts = fn.Split('.');
                return (Key: parts[parts.Length - 1], Value: (object)fn);
            });

            var symbolEntries = locals.Select(s => (Key: s.Name, Value: (object)s));
            var candidates = symbolEntries.Concat(typeEntries);

            // find similar identifiers
            var similar = StringSimilarity
                .FindSimilarSymbols(name, candidates)
                .Select(r => r.Value)
                .ToList();

            if (similar.Count == 0)
            {
                return;
            }

            // first item determines kind
            var entityKind = similar[0] != null ? SymbolFormatter.GetEntityKind(similar[0]) : "Identifier";

            // format all suggestions
            var formatted = similar.Select(v => v != null ? SymbolFormatter.FormatAny(v) : "").ToList();
            var suggestionText = "\n- " + string.Join("\n- ", formatted);

            var diag = Diagnostic.Create(
                Rule,
                id.GetLocation(),
                entityKind,
                name,
                suggestionText);

            context.ReportDiagnostic(diag);
        }
    }
}
