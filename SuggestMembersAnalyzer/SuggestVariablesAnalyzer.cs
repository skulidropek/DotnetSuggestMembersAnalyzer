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
    /// SuggestVariablesAnalyzer.
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

        private static readonly LocalizableString VariableTitle = new LocalizableResourceString(
            nameof(Resources.VariableNotFoundTitle),
            Resources.ResourceManager,
            typeof(Resources));

        private static readonly LocalizableString VariableDescription = new LocalizableResourceString(
            nameof(Resources.VariableNotFoundDescription),
            Resources.ResourceManager,
            typeof(Resources));

        private static readonly DiagnosticDescriptor VariableNotFoundRule = new DiagnosticDescriptor(
            VariableNotFoundDiagnosticId,
            VariableTitle,
            new LocalizableResourceString(
                nameof(Resources.VariableNotFoundMessageFormat),
                Resources.ResourceManager,
                typeof(Resources)),
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: VariableDescription,
            helpLinkUri: HelpLinkUri,
            customTags: "AnalyzerReleaseTracking");

        /// <inheritdoc/>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            [VariableNotFoundRule];

        /// <inheritdoc/>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeIdentifier, SyntaxKind.IdentifierName);
        }

        // Кэш всех имён типов в проекте
        private static ImmutableArray<string> _allTypeNames;

        private static ImmutableArray<string> GetAllTypeNames(Compilation compilation)
        {
            if (!_allTypeNames.IsDefaultOrEmpty)
            {
                return _allTypeNames;
            }

            var builder = ImmutableArray.CreateBuilder<string>();

            void CollectNamespace(INamespaceSymbol ns)
            {
                foreach (var type in ns.GetTypeMembers())
                {
                    string full = type
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        .Replace("global::", "");
                    builder.Add(full);
                    foreach (var nested in type.GetTypeMembers())
                    {
                        builder.Add(
                            nested
                            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            .Replace("global::", "")
                        );
                    }
                }

                foreach (var child in ns.GetNamespaceMembers())
                {
                    CollectNamespace(child);
                }
            }

            CollectNamespace(compilation.GlobalNamespace);

            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol asm)
                {
                    CollectNamespace(asm.GlobalNamespace);
                }
            }

            _allTypeNames = builder.Distinct().ToImmutableArray();
            return _allTypeNames;
        }

        private static void AnalyzeIdentifier(SyntaxNodeAnalysisContext context)
        {
            var id = (IdentifierNameSyntax)context.Node;
            var name = id.Identifier.ValueText;

            // НЕ ТРИГГЕРИТЬ для именованных аргументов вида `foo(bar: 123)`
            if (id.Parent is NameColonSyntax)
            {
                return;
            }

            // Фильтры: ключевые слова, nameof, декларации и т.п.
            if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ||
                name == "nameof" ||
                id.Parent is VariableDeclaratorSyntax ||
                id.Parent is ParameterSyntax ||
                id.Ancestors().OfType<UsingDirectiveSyntax>().Any() ||
                (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id) ||
                id.Parent is MemberBindingExpressionSyntax ||
                id.Ancestors()
                  .OfType<InvocationExpressionSyntax>()
                  .Any(inv => inv.Expression is IdentifierNameSyntax invId &&
                              invId.Identifier.ValueText == "nameof"))
            {
                return;
            }

            var model = context.SemanticModel;
            var info  = model.GetSymbolInfo(id);

            // Если символ найден или перегрузка неудачна, пропускаем
            if (info.Symbol != null ||
                info.CandidateReason == CandidateReason.OverloadResolutionFailure)
            {
                return;
            }

            // Собираем локальные символы
            var symbols = model.LookupSymbols(id.SpanStart)
                .Where(s => s.Kind is SymbolKind.Local
                         or SymbolKind.Parameter
                         or SymbolKind.Field
                         or SymbolKind.Property
                         or SymbolKind.Method)
                .ToList();

            // + все типы
            var typeNames = GetAllTypeNames(context.Compilation);

            var symbolEntries = symbols.Select(s => (Key: s.Name, Value: (object)s));
            var typeEntries   = typeNames.Select(fn =>
            {
                var parts = fn.Split('.');
                return (Key: parts[parts.Length - 1], Value: (object)fn);
            });
            var entries = symbolEntries.Concat(typeEntries).ToList();

            var similar = Utils.StringSimilarity
                .FindSimilarSymbols(name, entries)
                .Select(r => r.Value)
                .ToList();

            if (!similar.Any())
            {
                return;
            }

            // Определяем вид (Class, Local, Field, Property, Method)
            var entityKind = similar[0] != null ? SymbolFormatter.GetEntityKind(similar[0]) : "Identifier";

            // Форматируем подсказки
            var formatted = similar.Select(v =>
            {
                if (v is ISymbol sym)
                {
                    return SymbolFormatter.FormatSymbol(sym);
                }

                return "[Class] " + v;
            }).ToList();

            var suggestionText = "\n- " + string.Join("\n- ", formatted);

            var diagnostic = Diagnostic.Create(
                VariableNotFoundRule,
                id.GetLocation(),
                entityKind,
                name,
                suggestionText);

            context.ReportDiagnostic(diagnostic);
        }
    }
}
