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
    /// Roslyn analyzer that detects use of non-existent members (including in object initializers)
    /// and suggests similarly named fields/properties/methods.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SuggestMembersAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for member not found errors.
        /// </summary>
        public const string MemberNotFoundDiagnosticId = "SMB001";

        private const string Category = "Usage";
        private const string HelpLinkUri = "https://github.com/skulidropek/DotnetSuggestMembersAnalyzer";

        private static readonly LocalizableString MemberTitle =
            new LocalizableResourceString(
                nameof(Resources.MemberNotFoundTitle),
                Resources.ResourceManager,
                typeof(Resources));

        private static readonly LocalizableString MemberDescription =
            new LocalizableResourceString(
                nameof(Resources.MemberNotFoundDescription),
                Resources.ResourceManager,
                typeof(Resources));

        private static readonly DiagnosticDescriptor MemberNotFoundRule =
            new DiagnosticDescriptor(
                MemberNotFoundDiagnosticId,
                MemberTitle,
                new LocalizableResourceString(
                    nameof(Resources.MemberNotFoundMessageFormat),
                    Resources.ResourceManager,
                    typeof(Resources)),
                Category,
                DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description: MemberDescription,
                helpLinkUri: HelpLinkUri,
                customTags: "AnalyzerReleaseTracking");

        /// <summary>
        /// Gets the supported diagnostics.
        /// </summary>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(MemberNotFoundRule);

        /// <summary>
        /// Initializes the analyzer.
        /// </summary>
        /// <param name="context">The analysis context.</param>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            // 1) Handle obj.Member and obj?.Member
            context.RegisterSyntaxNodeAction(
                ctx => ReportMissingMember(ctx, restrictToFieldsAndProps: false),
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxKind.MemberBindingExpression);

            // 2) Handle object initializers:
            //    a) top-level: new T { Prop = value, ... }
            //    b) nested:        { SubProp = value, ... } after Prop = { ... }
            context.RegisterSyntaxNodeAction(
                AnalyzeInitializerAssignment,
                SyntaxKind.SimpleAssignmentExpression);
        }

        private static void AnalyzeInitializerAssignment(SyntaxNodeAnalysisContext context)
        {
            var assign = (AssignmentExpressionSyntax)context.Node;
            var init = assign.Parent as InitializerExpressionSyntax;
            if (init == null)
            {
                return;
            }

            // top-level initializer: parent is new T { ... }
            if (init.Parent is ObjectCreationExpressionSyntax creation &&
                assign.Left is IdentifierNameSyntax topName)
            {
                ReportMissingMember(context,
                    restrictToFieldsAndProps: true,
                    memberNameSyntax: topName,
                    targetExpression: creation);
            }
            // nested initializer: parent is Prop = { ... }
            else if (init.Parent is AssignmentExpressionSyntax parentAssign &&
                     assign.Left is IdentifierNameSyntax nestedName &&
                     parentAssign.Left is ExpressionSyntax parentTarget)
            {
                ReportMissingMember(context,
                    restrictToFieldsAndProps: true,
                    memberNameSyntax: nestedName,
                    targetExpression: parentTarget);
            }
        }

        private static void ReportMissingMember(
            SyntaxNodeAnalysisContext context,
            bool restrictToFieldsAndProps,
            SimpleNameSyntax? memberNameSyntax = null,
            ExpressionSyntax? targetExpression = null)
        {
            // discover syntax if not provided
            if (memberNameSyntax is null || targetExpression is null)
            {
                if (context.Node is MemberAccessExpressionSyntax m)
                {
                    if (m.Parent is AttributeSyntax)
                    {
                        return;
                    }

                    memberNameSyntax = m.Name;
                    targetExpression = m.Expression;
                }
                else if (context.Node is MemberBindingExpressionSyntax b &&
                         b.Parent is ConditionalAccessExpressionSyntax c)
                {
                    memberNameSyntax = b.Name;
                    targetExpression = c.Expression;
                }
            }
            if (memberNameSyntax is null || targetExpression is null)
            {
                return;
            }

            string memberName = memberNameSyntax.Identifier.ValueText;
            var model = context.SemanticModel;
            var info  = model.GetSymbolInfo(memberNameSyntax);

            // skip real symbols, overload failures, late bound, or methods with candidates
            if (info.Symbol != null
                || info.CandidateReason == CandidateReason.OverloadResolutionFailure
                || info.CandidateReason == CandidateReason.LateBound
                || (info.CandidateSymbols.Length > 0 && info.CandidateSymbols.Any(s => s.Kind == SymbolKind.Method)))
            {
                return;
            }

            // determine compile-time type
            var tinfo = model.GetTypeInfo(targetExpression);
            ITypeSymbol? exprType = tinfo.Type ?? tinfo.ConvertedType;
            if (exprType == null)
            {
                var sym = model.GetSymbolInfo(targetExpression).Symbol;
                exprType = sym switch
                {
                    ILocalSymbol    loc => loc.Type,
                    IFieldSymbol    fld => fld.Type,
                    IPropertySymbol prp => prp.Type,
                    IParameterSymbol par => par.Type,
                    IMethodSymbol   mth => mth.ReturnType,
                    _                   => null
                };
            }
            if (exprType == null)
            {
                return;
            }

            // collect all members (including bases/interfaces)
            var allMembers = new List<ISymbol>();
            void Collect(ITypeSymbol t)
            {
                allMembers.AddRange(t.GetMembers());
                foreach (var iface in t.AllInterfaces)
                {
                    allMembers.AddRange(iface.GetMembers());
                }

                if (t.BaseType != null)
                {
                    Collect(t.BaseType);
                }
            }
            Collect(exprType);

            // filter out implicit, accessors, dupes, and if requested only fields & props
            var seen    = new HashSet<string>();
            var entries = new List<(string Key, ISymbol Value)>();
            foreach (var sym in allMembers)
            {
                if (sym.IsImplicitlyDeclared
                    || sym.Name.StartsWith("get_", StringComparison.Ordinal)
                    || sym.Name.StartsWith("set_", StringComparison.Ordinal)
                    || !seen.Add(sym.Name))
                {
                    continue;
                }

                if (restrictToFieldsAndProps
                    && sym.Kind != SymbolKind.Field
                    && sym.Kind != SymbolKind.Property)
                {
                    continue;
                }

                entries.Add((sym.Name, sym));
            }

            // find up to 5 similar
            var similar = Utils.StringSimilarity
                              .FindSimilarSymbols(memberName, entries)
                              ;
                              
            if (similar.Count == 0)
            {
                return;
            }

            // format suggestions
            var formatted = similar
                .Select(r => SymbolFormatter.GetFormattedMemberRepresentation(r.Value, includeSignature: true))
                .ToList();
            var suggestionsText = "\n- " + string.Join("\n- ", formatted);
            var names           = similar.Select(r => r.Name).ToList();
            var props           = new Dictionary<string, string?> { ["Suggestions"] = string.Join("|", names) }
                                  .ToImmutableDictionary();

            // report
            var diag = Diagnostic.Create(
                MemberNotFoundRule,
                memberNameSyntax.GetLocation(),
                props,
                memberName,
                exprType.Name,
                suggestionsText);

            context.ReportDiagnostic(diag);
        }
    }
}
