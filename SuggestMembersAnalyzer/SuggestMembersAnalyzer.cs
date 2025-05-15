using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SuggestMembersAnalyzer
{
    /// <summary>
    /// Roslyn analyzer that detects use of non-existent members and suggests similar names.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public partial class SuggestMembersAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for member not found errors.
        /// </summary>
        public const string MemberNotFoundDiagnosticId = "SMB001";

        private static readonly LocalizableString MemberTitle = new LocalizableResourceString(
            nameof(Resources.MemberNotFoundTitle),
            Resources.ResourceManager,
            typeof(Resources));
        private static readonly LocalizableString MemberDescription = new LocalizableResourceString(
            nameof(Resources.MemberNotFoundDescription),
            Resources.ResourceManager,
            typeof(Resources));

        private const string HelpLinkUri = "https://github.com/skulidropek/DotnetSuggestMembersAnalyzer";
        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor MemberNotFoundRule = new DiagnosticDescriptor(
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

        private static readonly ImmutableArray<DiagnosticDescriptor> _supportedDiagnostics =
            ImmutableArray.Create(MemberNotFoundRule);

        /// <inheritdoc/>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => _supportedDiagnostics;

        /// <inheritdoc/>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            // handle both obj.Member and obj?.Member
            context.RegisterSyntaxNodeAction(
                AnalyzeMemberAccess,
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxKind.MemberBindingExpression);
        }

        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            ExpressionSyntax? targetExpression;
            SimpleNameSyntax memberNameSyntax;

            // 1) direct access: player.IsConnec1ted
            if (context.Node is MemberAccessExpressionSyntax m)
            {
                if (m.Parent is AttributeSyntax)
                {
                    return;
                }

                targetExpression   = m.Expression;
                memberNameSyntax   = m.Name;
            }
            // 2) conditional: player?.IsConnec1ted
            else if (context.Node is MemberBindingExpressionSyntax b &&
                     b.Parent is ConditionalAccessExpressionSyntax c)
            {
                targetExpression   = c.Expression;
                memberNameSyntax   = b.Name;
            }
            else
            {
                return;
            }

            string memberName = memberNameSyntax.Identifier.Text;
            var model        = context.SemanticModel;
            var info         = model.GetSymbolInfo(memberNameSyntax);

            if (info.Symbol != null
                || info.CandidateReason == CandidateReason.OverloadResolutionFailure
                || info.CandidateReason == CandidateReason.LateBound
                || (info.CandidateSymbols.Length > 0 && info.CandidateSymbols.Any(s => s.Kind == SymbolKind.Method)))
            {
                return;
            }

            // determine the compile-time type of the target
            var typeInfo   = model.GetTypeInfo(targetExpression);
            ITypeSymbol? exprType = typeInfo.Type ?? typeInfo.ConvertedType;
            if (exprType == null)
            {
                var exprSym = model.GetSymbolInfo(targetExpression).Symbol;
                if (exprSym != null)
                {
                    exprType = exprSym switch
                    {
                        ILocalSymbol p     => p.Type,
                        IFieldSymbol f     => f.Type,
                        IPropertySymbol q  => q.Type,
                        IParameterSymbol r => r.Type,
                        IMethodSymbol ms   => ms.ReturnType,
                        _                  => null
                    };
                }
            }
            if (exprType == null)
            {
                return;
            }

            // collect all members of the type, its bases and interfaces
            var allMembers = new List<ISymbol>();
            void CollectType(ITypeSymbol t)
            {
                allMembers.AddRange(t.GetMembers());
                foreach (var iface in t.AllInterfaces)
                {
                    allMembers.AddRange(iface.GetMembers());
                }

                if (t.BaseType != null)
                {
                    CollectType(t.BaseType);
                }
            }
            CollectType(exprType);

            // filter out implicit, accessors, duplicates
            var seen = new HashSet<string>();
            var entries = new List<(string Key, ISymbol Value)>();
            foreach (var sym in allMembers)
            {
                if (sym.IsImplicitlyDeclared
                    || sym.Name.StartsWith("get_")
                    || sym.Name.StartsWith("set_")
                    || !seen.Add(sym.Name))
                {
                    continue;
                }
                entries.Add((sym.Name, sym));
            }

            // find up to 5 similar members
            var similar = Utils.StringSimilarity
                              .FindSimilarSymbols(memberName, entries)
                              .Take(5)
                              .ToList();

            if (similar.Count == 0)
            {
                // fallback: lookup in current context symbols
                var symbolList = model.LookupSymbols(memberNameSyntax.SpanStart)
                                      .Select(s => (s.Name, s))
                                      .ToList();
                similar = Utils.StringSimilarity
                              .FindSimilarSymbols(memberName, symbolList)
                              .Take(5)
                              .ToList();

                if (similar.Count == 0)
                {
                    // fallback: all identifier tokens
                    var identifierEntries = memberNameSyntax.SyntaxTree.GetRoot()
                        .DescendantTokens()
                        .Where(t => t.IsKind(SyntaxKind.IdentifierToken))
                        .Select(t => t.ValueText)
                        .Where(txt => !string.IsNullOrEmpty(txt))
                        .Distinct()
                        .Select(txt => (txt, (ISymbol)null!))
                        .ToList();
                    similar = Utils.StringSimilarity
                                  .FindSimilarSymbols(memberName, identifierEntries)
                                  .Take(5)
                                  .ToList();
                }
            }

            if (similar.Count == 0)
            {
                return;
            }

            // format suggestions
            var suggestions = similar
                .Select(r => r.Value != null
                              ? GetFormattedMemberRepresentation(r.Value, includeSignature: true)
                              : r.Name)
                .ToList();
            var names = similar.Select(r => r.Name).ToList();
            var suggestionsText = "\n- " + string.Join("\n- ", suggestions);

            var props = new Dictionary<string, string?> { ["Suggestions"] = string.Join("|", names) }
                        .ToImmutableDictionary();

            var diagnostic = Diagnostic.Create(
                MemberNotFoundRule,
                memberNameSyntax.GetLocation(),
                props,
                memberName,
                exprType.Name,
                suggestionsText);

            context.ReportDiagnostic(diagnostic);
        }

        private static string GetMethodSignature(IMethodSymbol method)
        {
            try
            {
                if (method.MethodKind == MethodKind.PropertyGet && method.Name.StartsWith("get_"))
                {
                    string prop = method.Name.Substring(4);
                    var psym = method.ContainingType.GetMembers(prop).OfType<IPropertySymbol>().FirstOrDefault();
                    return psym != null
                        ? GetPropertySignature(psym)
                        : $"{GetFormattedTypeName(method.ReturnType)} {prop} {{ get; }}";
                }

                if (method.MethodKind == MethodKind.PropertySet && method.Name.StartsWith("set_"))
                {
                    string prop = method.Name.Substring(4);
                    var psym = method.ContainingType.GetMembers(prop).OfType<IPropertySymbol>().FirstOrDefault();
                    if (psym != null)
                    {
                        return GetPropertySignature(psym);
                    }

                    var ptype = method.Parameters.FirstOrDefault()?.Type ?? method.ContainingType;
                    return $"{GetFormattedTypeName(ptype)} {prop} {{ set; }}";
                }

                var sb = new StringBuilder();
                sb.Append(GetFormattedTypeName(method.ReturnType))
                  .Append(' ')
                  .Append(method.Name);

                if (method.IsGenericMethod && method.TypeParameters.Length > 0)
                {
                    sb.Append('<')
                      .Append(string.Join(", ", method.TypeParameters.Select(tp => tp.Name)))
                      .Append('>');
                }

                sb.Append('(')
                  .Append(string.Join(", ",
                      method.Parameters.Select(p =>
                      {
                          var mod = p.RefKind switch
                          {
                              RefKind.Ref => "ref ",
                              RefKind.Out => "out ",
                              RefKind.In  => "in ",
                              _           => ""
                          };
                          return $"{mod}{GetFormattedTypeName(p.Type)} {p.Name}";
                      })))
                  .Append(')');

                return sb.ToString();
            }
            catch
            {
                return method.Name + "()";
            }
        }

        private static string GetPropertySignature(IPropertySymbol property)
        {
            try
            {
                var sb = new StringBuilder();
                if (property.IsStatic)
                {
                    sb.Append("static ");
                }

                if (property.IsAbstract)
                {
                    sb.Append("abstract ");
                }

                if (property.IsVirtual)
                {
                    sb.Append("virtual ");
                }

                if (property.IsOverride)
                {
                    sb.Append("override ");
                }

                sb.Append(GetFormattedTypeName(property.Type))
                  .Append(' ')
                  .Append(property.Name)
                  .Append(" { ");
                if (property.GetMethod != null)
                {
                    sb.Append("get; ");
                }

                if (property.SetMethod != null)
                {
                    sb.Append("set; ");
                }

                sb.Append('}');
                return sb.ToString();
            }
            catch
            {
                return property.Name;
            }
        }

        private static string GetFormattedMemberRepresentation(ISymbol member, bool includeSignature)
        {
            if (!includeSignature)
            {
                return member.Name;
            }

            try
            {
                return member switch
                {
                    IMethodSymbol   m => GetMethodSignature(m),
                    IPropertySymbol p => GetPropertySignature(p),
                    IFieldSymbol    f => $"{(f.IsStatic ? "static " : "")}{GetFormattedTypeName(f.Type)} {f.Name}",
                    _                  => member.Name
                };
            }
            catch
            {
                return member.Name;
            }
        }

        private static string GetFormattedTypeName(ITypeSymbol type)
        {
            if (type == null)
            {
                return "object";
            }

            var fmt = SymbolDisplayFormat.MinimallyQualifiedFormat
                .WithMemberOptions(SymbolDisplayMemberOptions.None)
                .WithKindOptions(SymbolDisplayKindOptions.None)
                .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes)
                .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters);
            return type.ToDisplayString(fmt);
        }
    }
}
