using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SuggestMembersAnalyzer
{
    /// <summary>
    /// Roslyn analyzer that detects use of non-existent members and variables and suggests similar names.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public partial class SuggestMembersAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>
        /// Diagnostic ID for member not found errors
        /// </summary>
        public const string MemberNotFoundDiagnosticId = "SMB001";

        /// <summary>
        /// Diagnostic ID for variable not found errors
        /// </summary>
        public const string VariableNotFoundDiagnosticId = "SMB002";

        /// <summary>
        /// Diagnostic ID for namespace not found errors
        /// </summary>
        public const string NamespaceNotFoundDiagnosticId = "SMB003";

        // Define the categories and message formats
        private static readonly LocalizableString MemberTitle = new LocalizableResourceString(
            nameof(Resources.MemberNotFoundTitle), 
            Resources.ResourceManager, 
            typeof(Resources));

        private static readonly LocalizableString MemberDescription = new LocalizableResourceString(
            nameof(Resources.MemberNotFoundDescription), 
            Resources.ResourceManager, 
            typeof(Resources));

        private static readonly LocalizableString VariableTitle = new LocalizableResourceString(
            nameof(Resources.VariableNotFoundTitle), 
            Resources.ResourceManager, 
            typeof(Resources));

        private static readonly LocalizableString VariableDescription = new LocalizableResourceString(
            nameof(Resources.VariableNotFoundDescription), 
            Resources.ResourceManager, 
            typeof(Resources));

        private static readonly LocalizableString NamespaceTitle = new LocalizableResourceString(
            nameof(Resources.NamespaceNotFoundTitle),
            Resources.ResourceManager,
            typeof(Resources));

        private static readonly LocalizableString NamespaceDescription = new LocalizableResourceString(
            nameof(Resources.NamespaceNotFoundDescription),
            Resources.ResourceManager,
            typeof(Resources));

        // Define category, help link, and release track for analyzer
        private const string HelpLinkUri = "https://github.com/skulidropek/DotnetSuggestMembersAnalyzer";
        private const string Category = "Usage";

        // Define diagnostics that this analyzer reports
        private static readonly DiagnosticDescriptor MemberNotFoundRule = new DiagnosticDescriptor(
            MemberNotFoundDiagnosticId,
            MemberTitle,
            new LocalizableResourceString(nameof(Resources.MemberNotFoundMessageFormat), Resources.ResourceManager, typeof(Resources)),
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: MemberDescription,
            helpLinkUri: HelpLinkUri,
            customTags: "AnalyzerReleaseTracking");

        private static readonly DiagnosticDescriptor NamespaceNotFoundRule = new DiagnosticDescriptor(
            NamespaceNotFoundDiagnosticId,
            NamespaceTitle,
            new LocalizableResourceString(nameof(Resources.NamespaceNotFoundMessageFormat), Resources.ResourceManager, typeof(Resources)),
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: NamespaceDescription,
            helpLinkUri: HelpLinkUri,
            customTags: "AnalyzerReleaseTracking");

        private static readonly DiagnosticDescriptor VariableNotFoundRule = new DiagnosticDescriptor(
            VariableNotFoundDiagnosticId,
            VariableTitle,
            new LocalizableResourceString(nameof(Resources.VariableNotFoundMessageFormat), Resources.ResourceManager, typeof(Resources)),
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: VariableDescription,
            helpLinkUri: HelpLinkUri,
            customTags: "AnalyzerReleaseTracking");

        /// <summary>
        /// The collection of diagnostic descriptors supported by this analyzer
        /// </summary>
        private static readonly ImmutableArray<DiagnosticDescriptor> _supportedDiagnostics =
            [MemberNotFoundRule, NamespaceNotFoundRule, VariableNotFoundRule];

        /// <summary>
        /// Gets the set of supported diagnostics for this analyzer
        /// </summary>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => _supportedDiagnostics;

        /// <summary>
        /// Initializes the analyzer with the given analysis context
        /// </summary>
        /// <param name="context">Analysis context to register syntax node actions</param>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            // Register for member access expressions (e.g. obj.Method())
            context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
            
            // Register for identifiers that might be undefined variables
            context.RegisterSyntaxNodeAction(AnalyzeIdentifier, SyntaxKind.IdentifierName);

            // Register for using directives that might have misspelled namespaces
            context.RegisterSyntaxNodeAction(AnalyzeUsingDirective, SyntaxKind.UsingDirective);

            // Register for qualified names that might include class references
            context.RegisterSyntaxNodeAction(AnalyzeClassReference, SyntaxKind.QualifiedName);
        }

        // Common utility methods used across different analysis types
        private static ITypeSymbol? GetTypeFromSymbol(ISymbol symbol) => symbol switch
        {
            ILocalSymbol local => local.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            IParameterSymbol parameter => parameter.Type,
            IMethodSymbol method => method.ReturnType,
            _ => null
        };

        // Helper to format type names, handling generics properly
        private static string GetFormattedTypeName(ITypeSymbol type)
        {
            if (type == null)
            {
                return "object";
            }

            // Use built-in formatting for all types
            // Format: removes namespace, shows proper type name
            var format = SymbolDisplayFormat.MinimallyQualifiedFormat
                .WithMemberOptions(SymbolDisplayMemberOptions.None)
                .WithKindOptions(SymbolDisplayKindOptions.None)
                .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes)
                .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters);

            return type.ToDisplayString(format);
        }

        // Helper method to format variable with its type
        private static string FormatVariableWithType(string variableName, ITypeSymbol? type)
        {
            if (type == null)
            {
                return variableName;
            }

            return $"{GetFormattedTypeName(type)} {variableName}";
        }

        private static SyntaxNode? GetLocalScope(SyntaxNode node)
        {
            // Find the enclosing method or property declaration
            var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (method != null)
            {
                return method;
            }

            var property = node.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
            if (property != null)
            {
                return property;
            }

            var constructor = node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
            if (constructor != null)
            {
                return constructor;
            }

            var accessor = node.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
            if (accessor != null)
            {
                return accessor;
            }

            // Fall back to the containing type
            var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (type != null)
            {
                return type;
            }

            // If all else fails, return the compilation unit
            return node.SyntaxTree.GetCompilationUnitRoot();
        }
    }
} 