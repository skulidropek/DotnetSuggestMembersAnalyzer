using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SuggestMembersAnalyzer.Utils;

namespace SuggestMembersAnalyzer
{
    /// <summary>
    /// Roslyn analyzer that reports an error when a member access cannot be resolved
    /// and suggests up to five of the most similar existing members.
    ///
    /// Behavior summary:
    ///  • Instance members → always considered.
    ///  • Extension methods → only those visible via using-directives in the current file.
    ///  • In object-initializers → suggestions limited to fields and properties.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SuggestMembersAnalyzer : DiagnosticAnalyzer
    {
        #region Diagnostic metadata

        /// <summary>
        /// The diagnostic ID raised when a member is not found.
        /// </summary>
        public const string Id = "SMB001";

        private const string Category    = "Usage";
        private const string HelpLinkUri = "https://github.com/skulidropek/DotnetSuggestMembersAnalyzer";

        private static readonly DiagnosticDescriptor Rule = new(
            id:               Id,
            title:            new LocalizableResourceString(
                                  nameof(Resources.MemberNotFoundTitle),
                                  Resources.ResourceManager,
                                  typeof(Resources)),
            messageFormat:    new LocalizableResourceString(
                                  nameof(Resources.MemberNotFoundMessageFormat),
                                  Resources.ResourceManager,
                                  typeof(Resources)),
            category:         Category,
            defaultSeverity:  DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description:      new LocalizableResourceString(
                                  nameof(Resources.MemberNotFoundDescription),
                                  Resources.ResourceManager,
                                  typeof(Resources)),
            helpLinkUri:      HelpLinkUri,
            customTags:       "AnalyzerReleaseTracking");

        /// <inheritdoc/>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(Rule);

        #endregion

        #region Initialization

        /// <inheritdoc/>
        public override void Initialize(AnalysisContext context)
        {
            // Skip generated code, allow concurrent execution
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            // 1) Direct member access: obj.Member and obj?.Member
            context.RegisterSyntaxNodeAction(
                ctx => AnalyzeMemberAccess(ctx, restrictToFieldsAndProps: false),
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxKind.MemberBindingExpression);

            // 2) Object-initializer assignments: new T { Prop = … } and nested
            context.RegisterSyntaxNodeAction(
                AnalyzeObjectInitializerAssignment,
                SyntaxKind.SimpleAssignmentExpression);
        }

        #endregion

        #region Object-Initializer Support

        /// <summary>
        /// When inside an object initializer, only fields/properties should be suggested.
        /// Redirects to <see cref="AnalyzeMemberAccess"/> with <c>restrictToFieldsAndProps = true</c>.
        /// </summary>
        private static void AnalyzeObjectInitializerAssignment(SyntaxNodeAnalysisContext ctx)
        {
            var assign = (AssignmentExpressionSyntax)ctx.Node;
            if (assign.Parent is not InitializerExpressionSyntax init)
            {
                return;
            }

            // Top-level initializer: new T { Prop = value }
            if (init.Parent is ObjectCreationExpressionSyntax creation &&
                assign.Left is IdentifierNameSyntax propName)
            {
                AnalyzeMemberAccess(
                    ctx,
                    restrictToFieldsAndProps: true,
                    memberNameSyntax: propName,
                    targetExpression: creation);
            }
            // Nested initializer: ParentProp = { SubProp = value }
            else if (init.Parent is AssignmentExpressionSyntax parentAssign &&
                     assign.Left is IdentifierNameSyntax nestedName &&
                     parentAssign.Left is ExpressionSyntax nestedTarget)
            {
                AnalyzeMemberAccess(
                    ctx,
                    restrictToFieldsAndProps: true,
                    memberNameSyntax: nestedName,
                    targetExpression: nestedTarget);
            }
        }

        #endregion

        #region Extension-Method Discovery

        /// <summary>
        /// Adds into <paramref name="entries"/> all extension methods that:
        /// 1) are visible in scope via <c>using</c>-directives, and
        /// 2) can be applied to <paramref name="receiverType"/>.
        ///
        /// We first ask <see cref="SemanticModel.LookupSymbols"/>,
        /// then walk each namespace imported via <c>using</c> to catch
        /// any static extension classes missed by LookupSymbols.
        /// </summary>
        private static void AddVisibleExtensionMethods(
            ITypeSymbol                receiverType,
            SemanticModel              model,
            int                        position,
            HashSet<string>            seenNames,
            List<(string Name, ISymbol Symbol)> entries)
        {
            var compilation = model.Compilation;

            // --- 1) Fast path: symbols that LookupSymbols already makes visible ---
            foreach (var method in model
                         .LookupSymbols(position,
                                        container: null,
                                        name: null,
                                        includeReducedExtensionMethods: true)
                         .OfType<IMethodSymbol>())
            {
                TryAdd(method);
            }

            // --- 2) Walk each namespace imported via using-directive ----------
            var root = model.SyntaxTree.GetRoot(CancellationToken.None);
            var nsUsings = root
                .DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(u => !u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                         && u.Name is not null);

            foreach (var u in nsUsings)
            {
                if (model.GetSymbolInfo(u.Name!).Symbol is not INamespaceSymbol nsSymbol)
                {
                    continue;
                }

                // DFS: push namespace and nested static types
                var stack = new Stack<INamespaceOrTypeSymbol>();
                stack.Push(nsSymbol);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();

                    switch (current)
                    {
                        case INamespaceSymbol childNs:
                            // enqueue nested namespaces and types
                            foreach (var member in childNs.GetMembers())
                            {
                                stack.Push(member);
                            }

                            break;

                        case INamedTypeSymbol type when type.IsStatic:
                            // inspect each method in static type
                            foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
                            {
                                TryAdd(m);
                            }

                            break;
                    }
                }
            }

            // --- local helper ----------------------------------------------------
            void TryAdd(IMethodSymbol m)
            {
                IMethodSymbol? candidate;
                
                if (m.MethodKind == MethodKind.ReducedExtension)
                {
                    candidate = m;
                }
                else if (m.IsExtensionMethod)
                {
                    candidate = BindExtension(m);
                }
                else
                {
                    candidate = null;
                }

                if (candidate is null)
                {
                    return;  // skip if not applicable
                }

                if (!seenNames.Add(candidate.Name))
                {
                    return;  // skip duplicates
                }

                entries.Add((candidate.Name, candidate));
            }

            // Attempt to bind generic parameters, or fall back to conversion
            IMethodSymbol? BindExtension(IMethodSymbol ext)
            {
                // 1) Try Roslyn's inference
                var reduced = ext.ReduceExtensionMethod(receiverType);
                if (reduced is not null)
                {
                    return reduced;
                }

                // 2) Fallback: ensure receiverType → thisParam is convertible
                if (ext.Parameters.Length == 0)
                {
                    return null;
                }

                var thisParam = ext.Parameters[0].Type;
                var conv      = compilation.ClassifyConversion(receiverType, thisParam);
                return (conv.Exists && !conv.IsExplicit) ? ext : null;
            }
        }

        #endregion

        #region Main Analysis Pipeline

        /// <summary>
        /// Core routine: if <paramref name="memberNameSyntax"/> doesn't bind,
        /// gather instance + extension candidates, find similar names, and report SMB001.
        /// </summary>
        /// <param name="ctx">Analysis context</param>
        /// <param name="restrictToFieldsAndProps">
        ///   If true, only fields and properties will be suggested (object-initializer).
        /// </param>
        /// <param name="memberNameSyntax">
        ///   Optional: the simple name syntax node for the member. If null, it will be inferred.
        /// </param>
        /// <param name="targetExpression">
        ///   Optional: the expression syntax node whose type we are inspecting.
        /// </param>
        private static void AnalyzeMemberAccess(
            SyntaxNodeAnalysisContext ctx,
            bool                      restrictToFieldsAndProps,
            SimpleNameSyntax?         memberNameSyntax = null,
            ExpressionSyntax?         targetExpression  = null)
        {
            // 1) Resolve syntax nodes if not provided
            if (memberNameSyntax is null || targetExpression is null)
            {
                if (ctx.Node is MemberAccessExpressionSyntax ma && ma.Parent is not AttributeSyntax)
                {
                    memberNameSyntax = ma.Name;
                    targetExpression = ma.Expression;
                }
                else if (ctx.Node is MemberBindingExpressionSyntax mb &&
                         mb.Parent is ConditionalAccessExpressionSyntax ca)
                {
                    memberNameSyntax = mb.Name;
                    targetExpression = ca.Expression;
                }
            }
            if (memberNameSyntax is null || targetExpression is null)
            {
                return;
            }

            // Extract the unknown identifier
            string missing = memberNameSyntax.Identifier.ValueText;
            var    model   = ctx.SemanticModel;

            // 2) If symbol exists or overload candidates exist → bail out
            var symInfo = model.GetSymbolInfo(memberNameSyntax);
            if (symInfo.Symbol != null
                || symInfo.CandidateReason == CandidateReason.OverloadResolutionFailure
                || symInfo.CandidateSymbols.Any(s => s.Kind == SymbolKind.Method))
            {
                return;
            }

            // 3) Determine compile-time type of the target expression
            var tInfo    = model.GetTypeInfo(targetExpression);
            var exprType = tInfo.Type ?? tInfo.ConvertedType
                         ?? (model.GetSymbolInfo(targetExpression).Symbol switch
                         {
                             ILocalSymbol    loc => loc.Type,
                             IFieldSymbol    fld => fld.Type,
                             IPropertySymbol prp => prp.Type,
                             IParameterSymbol par => par.Type,
                             IMethodSymbol   mth => mth.ReturnType,
                             _                   => null
                         });
            if (exprType is null)
            {
                return;
            }

            // 4) Collect instance members (fields, props, methods)
            var entries = new List<(string Name, ISymbol Symbol)>();
            var seen    = new HashSet<string>(StringComparer.Ordinal);

            void Add(ISymbol sym)
            {
                if (sym.IsImplicitlyDeclared
                    || sym.Name.StartsWith("get_", StringComparison.Ordinal)
                    || sym.Name.StartsWith("set_", StringComparison.Ordinal)
                    || !seen.Add(sym.Name))
                {
                    return;
                }

                if (restrictToFieldsAndProps
                    && sym.Kind is not (SymbolKind.Field or SymbolKind.Property))
                {
                    return;
                }

                entries.Add((sym.Name, sym));
            }

            void Collect(ITypeSymbol type)
            {
                foreach (var mem in type.GetMembers())
                {
                    Add(mem);
                }

                foreach (var iface in type.AllInterfaces)
                {
                    foreach (var mem in iface.GetMembers())
                    {
                        Add(mem);
                    }
                }

                if (type.BaseType is not null)
                {
                    Collect(type.BaseType);
                }
            }
            Collect(exprType);

            // 5) Add extension methods if allowed
            if (!restrictToFieldsAndProps)
            {
                AddVisibleExtensionMethods(
                    receiverType: exprType,
                    model:         model,
                    position:      memberNameSyntax.SpanStart,
                    seenNames:     seen,
                    entries:       entries);
            }

            // 6) Find up to five closest matches
            var similar = Utils.StringSimilarity.FindSimilarSymbols(missing, entries);
            if (similar.Count == 0)
            {
                return;
            }

            // 7) Format the suggestions and report diagnostic
            var lines = similar
                .Select(r => SymbolFormatter.GetFormattedMemberRepresentation(r.Value, includeSignature: true))
                .ToList();

            var props = new Dictionary<string, string?>
            {
                ["Suggestions"] = string.Join("|", similar.Select(r => r.Name))
            }.ToImmutableDictionary();

            ctx.ReportDiagnostic(
                Diagnostic.Create(
                    descriptor: Rule,
                    location:   memberNameSyntax.GetLocation(),
                    properties: props,
                    messageArgs: new object[] {
                        missing,               // {0}
                        exprType.Name,         // {1}
                        "\n- " + string.Join("\n- ", lines)  // {2}
                    }));
        }

        #endregion
    }
}
