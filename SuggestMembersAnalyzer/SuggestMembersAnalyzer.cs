// <copyright file="SuggestMembersAnalyzer.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Analyzers
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using global::SuggestMembersAnalyzer.Utils;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

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
        /// <summary>
        /// The diagnostic ID raised when a member is not found.
        /// </summary>
        public const string Id = "SMB001";

        private const string Category = "Usage";
        private const string HelpLinkUri = "https://github.com/skulidropek/DotnetSuggestMembersAnalyzer";

        private static readonly DiagnosticDescriptor Rule = new (
            id: Id,
            title: new LocalizableResourceString(
                nameof(Resources.MemberNotFoundTitle),
                Resources.ResourceManager,
                typeof(Resources)),
            messageFormat: new LocalizableResourceString(
                    nameof(Resources.MemberNotFoundMessageFormat),
                    Resources.ResourceManager,
                    typeof(Resources)),
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: new LocalizableResourceString(
                                  nameof(Resources.MemberNotFoundDescription),
                                  Resources.ResourceManager,
                                  typeof(Resources)),
            helpLinkUri: HelpLinkUri,
            customTags: "AnalyzerReleaseTracking");

        /// <inheritdoc/>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(Rule);

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

        /// <summary>
        /// When inside an object initializer, only fields/properties should be suggested.
        /// Redirects to <see cref="AnalyzeMemberAccess"/> with <c>restrictToFieldsAndProps = true</c>.
        /// </summary>
        /// <param name="ctx">Analysis context.</param>
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

        /// <summary>
        /// Adds into <paramref name="entries"/> all extension methods that:
        /// 1) are visible in scope via <c>using</c>-directives, and
        /// 2) can be applied to <paramref name="receiverType"/>.
        ///
        /// We first ask <see cref="SemanticModel.LookupSymbols"/>,
        /// then walk each namespace imported via <c>using</c> to catch
        /// any static extension classes missed by LookupSymbols.
        /// </summary>
        /// <param name="receiverType">Type to find extension methods for.</param>
        /// <param name="model">Semantic model for symbol resolution.</param>
        /// <param name="position">Position in syntax tree for scope resolution.</param>
        /// <param name="seenNames">Set to track already discovered method names.</param>
        /// <param name="entries">List to add discovered extension methods to.</param>
        private static void AddVisibleExtensionMethods(
            ITypeSymbol receiverType,
            SemanticModel model,
            int position,
            HashSet<string> seenNames,
            List<(string Name, ISymbol Symbol)> entries)
        {
            var compilation = model.Compilation;
            var context = new Utils.ExtensionMethodContext(receiverType, compilation, seenNames, entries);

            // 1) Fast path: symbols that LookupSymbols already makes visible
            AddLookupExtensionMethods(model, position, context);

            // 2) Walk each namespace imported via using-directive
            AddUsingNamespaceExtensionMethods(model, context);
        }

        /// <summary>
        /// Adds extension methods found through LookupSymbols.
        /// </summary>
        /// <param name="model">The semantic model for symbol resolution.</param>
        /// <param name="position">The position in syntax tree for scope resolution.</param>
        /// <param name="context">The extension method discovery context.</param>
        private static void AddLookupExtensionMethods(
            SemanticModel model,
            int position,
            ExtensionMethodContext context)
        {
            foreach (var method in model
                         .LookupSymbols(
                             position,
                             container: null,
                             name: null,
                             includeReducedExtensionMethods: true)
                         .OfType<IMethodSymbol>())
            {
                context.TryAdd(method);
            }
        }

        /// <summary>
        /// Adds extension methods from namespaces imported via using directives.
        /// </summary>
        /// <param name="model">The semantic model for symbol resolution.</param>
        /// <param name="context">The extension method discovery context.</param>
        private static void AddUsingNamespaceExtensionMethods(
            SemanticModel model,
            ExtensionMethodContext context)
        {
            var root = model.SyntaxTree.GetRoot(CancellationToken.None);
            var nsUsings = root
                .DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(u => !u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                         && u.Name is not null);

            foreach (var u in nsUsings)
            {
                if (model.GetSymbolInfo(u.Name!).Symbol is INamespaceSymbol nsSymbol)
                {
                    ProcessNamespaceForExtensions(nsSymbol, context);
                }
            }
        }

        /// <summary>
        /// Processes a namespace to find extension methods using DFS.
        /// </summary>
        /// <param name="rootNamespace">The namespace to process for extension methods.</param>
        /// <param name="context">The extension method discovery context.</param>
        private static void ProcessNamespaceForExtensions(
            INamespaceSymbol rootNamespace,
            ExtensionMethodContext context)
        {
            var stack = new Stack<INamespaceOrTypeSymbol>();
            stack.Push(rootNamespace);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                switch (current)
                {
                    case INamespaceSymbol childNs:
                        foreach (var member in childNs.GetMembers())
                        {
                            stack.Push(member);
                        }

                        break;

                    case INamedTypeSymbol type when type.IsStatic:
                        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
                        {
                            context.TryAdd(m);
                        }

                        break;

                    default:
                        // Ignore non-namespace, non-static types
                        break;
                }
            }
        }

        /// <summary>
        /// Core routine: if <paramref name="memberNameSyntax"/> doesn't bind,
        /// gather instance + extension candidates, find similar names, and report SMB001.
        /// </summary>
        /// <param name="ctx">Analysis context.</param>
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
            bool restrictToFieldsAndProps,
            SimpleNameSyntax? memberNameSyntax = null,
            ExpressionSyntax? targetExpression = null)
        {
            // 1) Resolve syntax nodes if not provided
            if (!TryResolveMemberAccess(ctx, ref memberNameSyntax, ref targetExpression))
            {
                return;
            }

            // Extract the unknown identifier and validate symbol info
            string missing = memberNameSyntax!.Identifier.ValueText;
            var model = ctx.SemanticModel;

            if (ShouldSkipMemberAccess(model, memberNameSyntax))
            {
                return;
            }

            // 3) Determine compile-time type of the target expression
            var exprType = GetTargetExpressionType(model, targetExpression!);
            if (exprType is null)
            {
                return;
            }

            // 4) Collect members and find suggestions
            var entries = CollectMemberCandidates(exprType, restrictToFieldsAndProps, out var seen);

            // 5) Add extension methods if allowed
            if (!restrictToFieldsAndProps)
            {
                AddVisibleExtensionMethods(exprType, model, memberNameSyntax.SpanStart, seen, entries);
            }

            // 6) Find similar symbols and report diagnostic
            var similar = Utils.StringSimilarity.FindSimilarSymbols(missing, entries);
            if (similar.Count > 0)
            {
                ReportMemberAccessDiagnostic(ctx, memberNameSyntax, missing, exprType, similar);
            }
        }

        /// <summary>
        /// Attempts to resolve member access syntax nodes from the analysis context.
        /// </summary>
        /// <param name="ctx">The analysis context.</param>
        /// <param name="memberNameSyntax">The member name syntax to resolve.</param>
        /// <param name="targetExpression">The target expression syntax to resolve.</param>
        /// <returns>True if both syntax nodes were successfully resolved.</returns>
        private static bool TryResolveMemberAccess(
            SyntaxNodeAnalysisContext ctx,
            ref SimpleNameSyntax? memberNameSyntax,
            ref ExpressionSyntax? targetExpression)
        {
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

            return memberNameSyntax is not null && targetExpression is not null;
        }

        /// <summary>
        /// Determines if member access analysis should be skipped.
        /// </summary>
        /// <param name="model">The semantic model.</param>
        /// <param name="memberNameSyntax">The member name syntax to check.</param>
        /// <returns>True if analysis should be skipped.</returns>
        private static bool ShouldSkipMemberAccess(SemanticModel model, SimpleNameSyntax memberNameSyntax)
        {
            var symInfo = model.GetSymbolInfo(memberNameSyntax);
            return symInfo.Symbol != null
                || symInfo.CandidateReason == CandidateReason.OverloadResolutionFailure
                || symInfo.CandidateSymbols.Any(s => s.Kind == SymbolKind.Method);
        }

        /// <summary>
        /// Gets the type of the target expression for member access.
        /// </summary>
        /// <param name="model">The semantic model.</param>
        /// <param name="targetExpression">The target expression to analyze.</param>
        /// <returns>The type symbol of the target expression, or null if not resolvable.</returns>
        private static ITypeSymbol? GetTargetExpressionType(SemanticModel model, ExpressionSyntax targetExpression)
        {
            var tInfo = model.GetTypeInfo(targetExpression);
            return tInfo.Type ?? tInfo.ConvertedType
                         ?? model.GetSymbolInfo(targetExpression).Symbol switch
                         {
                             ILocalSymbol loc => loc.Type,
                             IFieldSymbol fld => fld.Type,
                             IPropertySymbol prp => prp.Type,
                             IParameterSymbol par => par.Type,
                             IMethodSymbol mth => mth.ReturnType,
                             _ => null
                         };
        }

        /// <summary>
        /// Collects member candidates from the target type and its hierarchy.
        /// </summary>
        /// <param name="exprType">The expression type to collect members from.</param>
        /// <param name="restrictToFieldsAndProps">Whether to restrict to fields and properties only.</param>
        /// <param name="seen">Output parameter for tracking seen member names.</param>
        /// <returns>List of member candidates.</returns>
        private static List<(string Name, ISymbol Symbol)> CollectMemberCandidates(
            ITypeSymbol exprType,
            bool restrictToFieldsAndProps,
            out HashSet<string> seen)
        {
            var entries = new List<(string Name, ISymbol Symbol)>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);

            CollectMembersRecursive(exprType, entries, seenNames, restrictToFieldsAndProps);

            seen = seenNames;
            return entries;
        }

        /// <summary>
        /// Recursively collects members from a type and its base types/interfaces.
        /// </summary>
        /// <param name="type">The type to collect members from.</param>
        /// <param name="entries">The list to add entries to.</param>
        /// <param name="seenNames">Set of already seen member names.</param>
        /// <param name="restrictToFieldsAndProps">Whether to restrict to fields and properties only.</param>
        private static void CollectMembersRecursive(
            ITypeSymbol type,
            List<(string Name, ISymbol Symbol)> entries,
            HashSet<string> seenNames,
            bool restrictToFieldsAndProps)
        {
            foreach (var mem in type.GetMembers())
            {
                AddMemberIfValid(mem, entries, seenNames, restrictToFieldsAndProps);
            }

            foreach (var iface in type.AllInterfaces)
            {
                foreach (var mem in iface.GetMembers())
                {
                    AddMemberIfValid(mem, entries, seenNames, restrictToFieldsAndProps);
                }
            }

            if (type.BaseType is not null)
            {
                CollectMembersRecursive(type.BaseType, entries, seenNames, restrictToFieldsAndProps);
            }
        }

        /// <summary>
        /// Adds a member to the entries list if it passes validation.
        /// </summary>
        /// <param name="sym">The symbol to validate and add.</param>
        /// <param name="entries">The list to add the entry to.</param>
        /// <param name="seenNames">Set of already seen member names.</param>
        /// <param name="restrictToFieldsAndProps">Whether to restrict to fields and properties only.</param>
        private static void AddMemberIfValid(
            ISymbol sym,
            List<(string Name, ISymbol Symbol)> entries,
            HashSet<string> seenNames,
            bool restrictToFieldsAndProps)
        {
            if (sym.IsImplicitlyDeclared
                || sym.Name.StartsWith("get_", StringComparison.Ordinal)
                || sym.Name.StartsWith("set_", StringComparison.Ordinal)
                || !seenNames.Add(sym.Name))
            {
                return;
            }

            if (restrictToFieldsAndProps
                && sym.Kind is not(SymbolKind.Field or SymbolKind.Property))
            {
                return;
            }

            entries.Add((sym.Name, sym));
        }

        /// <summary>
        /// Reports a member access diagnostic with suggestions.
        /// </summary>
        /// <param name="ctx">The analysis context.</param>
        /// <param name="memberNameSyntax">The member name syntax with the error.</param>
        /// <param name="missing">The missing member name.</param>
        /// <param name="exprType">The target expression type.</param>
        /// <param name="similar">The similar symbol suggestions.</param>
        private static void ReportMemberAccessDiagnostic(
            SyntaxNodeAnalysisContext ctx,
            SimpleNameSyntax memberNameSyntax,
            string missing,
            ITypeSymbol exprType,
            IReadOnlyList<(string Name, ISymbol Value, double Score)> similar)
        {
            var lines = similar
                .Select(r => SymbolFormatter.GetFormattedMemberRepresentation(r.Value, includeSignature: true))
                .ToList();

            var props = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Suggestions"] = string.Join("|", similar.Select(r => r.Name)),
            }.ToImmutableDictionary();

            var messageArgs = new object[]
            {
                missing,               // {0}
                exprType.Name,         // {1}
                "\n- " + string.Join("\n- ", lines),  // {2}
            };

            ctx.ReportDiagnostic(
                Diagnostic.Create(
                    descriptor: Rule,
                    location: memberNameSyntax.GetLocation(),
                    properties: props,
                    messageArgs: messageArgs));
        }
    }
}
