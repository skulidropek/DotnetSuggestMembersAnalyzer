using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SuggestMembersAnalyzer
{
    // Part of the analyzer responsible for checking namespaces and using directives
    public partial class SuggestMembersAnalyzer
    {
        private static void AnalyzeUsingDirective(SyntaxNodeAnalysisContext context)
        {

            var usingDirective = (UsingDirectiveSyntax)context.Node;

            // Skip static usings and usings with alias
            if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) || usingDirective.Alias != null)
            {
                return;
            }

            // Get the full namespace text
            var namespaceName = usingDirective.Name?.ToString() ?? string.Empty;

            // Skip empty namespace names
            if (string.IsNullOrEmpty(namespaceName))
            {
                return;
            }

            // Check if there are errors in the namespace name
            var semanticModel = context.SemanticModel;

            // Check that Name is not null
            if (usingDirective.Name == null)
            {
                return;
            }

            var symbolInfo = semanticModel.GetSymbolInfo(usingDirective.Name);

            // If a symbol is found, everything is fine
            if (symbolInfo.Symbol != null)
            {

                return;
            }

            // Check if this might be a typo in a known namespace
            var similarNamespaces = GetSimilarNamespaces(context.Compilation, namespaceName);

            if (similarNamespaces.Count > 0)
            {
                // Create a list of suggestions
                var allSuggestions = new List<string>();
                var allFormattedSuggestions = new List<string>();

                foreach (var (correctedName, _) in similarNamespaces)
                {
                    // Exclude only exact matches
                    if (correctedName != namespaceName)
                    {
                        allSuggestions.Add(correctedName);
                        allFormattedSuggestions.Add(correctedName);
                    }
                }

                // Take first 5 suggestions
                allSuggestions = [.. allSuggestions.Take(5)];
                allFormattedSuggestions = [.. allFormattedSuggestions.Take(5)];

                if (allSuggestions.Count > 0)
                {
                    var suggestionsText = "\n- " + string.Join("\n- ", allFormattedSuggestions);

                    // Create diagnostics with suggestions
                    var properties = new Dictionary<string, string?>
                    {
                        { "Suggestions", string.Join("|", allSuggestions) }
                    }.ToImmutableDictionary();

                    // Check if there are any suggestions
                    if (suggestionsText.Length == 0)
                    {
                        return;
                    }

                    // Create diagnostic with namespace descriptor
                    var diagnostic = Diagnostic.Create(
                        NamespaceNotFoundRule,
                        usingDirective.Name.GetLocation(),
                        properties,
                        namespaceName,
                        suggestionsText);

                    context.ReportDiagnostic(diagnostic);
                }
            }
        }

        /// <summary>
        /// Finds similar namespaces in the current compilation
        /// </summary>
        private static List<(string namespaceName, double similarity)> GetSimilarNamespaces(Compilation compilation, string namespaceName)
        {
            // Result list of similar namespaces
            var result = new List<(string namespaceName, double similarity)>();

            // Get all available namespaces from the compilation
            var availableNamespaces = GetAllNamespaces(compilation);

            // Check both full namespace and individual parts
            var namespaceParts = namespaceName.Split('.');

            // First try to match against the full namespace name
            foreach (var ns in availableNamespaces)
            {
                double similarity = Utils.StringSimilarity.ComputeCompositeScore(namespaceName, ns);
                if (similarity >= 0.7) // Higher threshold for full namespace names
                {
                    result.Add((ns, similarity));
                }
            }

            // If we didn't find good matches, try to match against each namespace part
            if (result.Count == 0 && namespaceParts.Length > 1)
            {
                foreach (var ns in availableNamespaces)
                {
                    var nsParts = ns.Split('.');

                    if (nsParts.Length != namespaceParts.Length)
                    {
                        continue;
                    }

                    // Count matching parts and calculate average similarity
                    double totalSimilarity = 0;
                    int matchCount = 0;

                    for (int i = 0; i < namespaceParts.Length && i < nsParts.Length; i++)
                    {
                        double partSimilarity = Utils.StringSimilarity.ComputeCompositeScore(namespaceParts[i], nsParts[i]);
                        if (partSimilarity >= 0.6)
                        {
                            totalSimilarity += partSimilarity;
                            matchCount++;
                        }
                    }

                    // Only consider namespaces where most parts match
                    if (matchCount > 0 && (double)matchCount / namespaceParts.Length >= 0.5)
                    {
                        double avgSimilarity = totalSimilarity / matchCount;
                        result.Add((ns, avgSimilarity));
                    }
                }
            }

            // Sort by similarity score (highest first) and return top 5 results
            return [.. result
                .OrderByDescending(t => t.similarity)
                .Take(5)];
        }

        /// <summary>
        /// Gets all available namespaces from the compilation
        /// </summary>
        private static HashSet<string> GetAllNamespaces(Compilation compilation)
        {
            var namespaces = new HashSet<string>();

            // Add all referenced namespaces from the compilation
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                {
                    // Get the global namespace from the assembly
                    var globalNamespace = assembly.GlobalNamespace;
                    AddNamespacesRecursively(globalNamespace, namespaces);
                }
            }

            // Add source-code defined namespaces
            AddNamespacesRecursively(compilation.GlobalNamespace, namespaces);

            // Add using directives from all syntax trees
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetCompilationUnitRoot();
                foreach (var name in root.Usings.Select(u => u.Name).Where(n => n != null))
                {
                    if (name != null)
                    {
                        var usingName = name.ToString();
                        if (!string.IsNullOrEmpty(usingName))
                        {
                            namespaces.Add(usingName);
                        }
                    }
                }
            }

            return namespaces;
        }

        /// <summary>
        /// Recursively adds all nested namespaces
        /// </summary>
        private static void AddNamespacesRecursively(INamespaceSymbol namespaceSymbol, HashSet<string> namespaces)
        {
            // Skip the global namespace (it has an empty name)
            if (!string.IsNullOrEmpty(namespaceSymbol.Name))
            {
                namespaces.Add(namespaceSymbol.ToDisplayString());
            }

            // Recursively process all nested namespaces
            foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
            {
                AddNamespacesRecursively(nestedNamespace, namespaces);
            }
        }

        // Helper method to get common system types from the compilation
        private static List<INamedTypeSymbol> GetCommonSystemTypes(Compilation compilation)
        {
            var result = new List<INamedTypeSymbol>();

            // Look for types in all loaded assemblies
            AddTypesFromAssemblies(compilation, result);

            return result;
        }

        // Helper method to add common types from loaded assemblies
        private static void AddTypesFromAssemblies(Compilation compilation, List<INamedTypeSymbol> result)
        {
            // Look for types in all loaded assemblies
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                {
                    continue;
                }

                // Process the global namespace of the assembly - will recursively add all types
                ProcessNamespaceForTypes(assembly.GlobalNamespace, result);
            }

            // Also process types from the compilation's own assembly
            ProcessNamespaceForTypes(compilation.GlobalNamespace, result);
        }

        // Process a namespace and all its child namespaces to find types
        private static void ProcessNamespaceForTypes(INamespaceSymbol namespaceSymbol, List<INamedTypeSymbol> result)
        {
            // Add types directly from this namespace
            foreach (var member in namespaceSymbol.GetMembers())
            {
                if (member is INamedTypeSymbol typeSymbol &&
                    !result.Any(t => t.Equals(typeSymbol, SymbolEqualityComparer.Default)))
                {
                    result.Add(typeSymbol);

                    // Add nested types within this type
                    foreach (var nestedType in typeSymbol.GetTypeMembers())
                    {
                        if (!result.Any(t => t.Equals(nestedType, SymbolEqualityComparer.Default)))
                        {
                            result.Add(nestedType);
                        }
                    }
                }
            }

            // Recursively process child namespaces
            foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
            {
                ProcessNamespaceForTypes(childNamespace, result);
            }
        }

        // Helper method to recursively add types from a namespace
        private static void AddTypesFromNamespace(INamespaceSymbol namespaceSymbol, List<INamedTypeSymbol> typeSymbols)
        {
            foreach (var member in namespaceSymbol.GetMembers())
            {
                if (member is INamedTypeSymbol typeSymbol)
                {
                    typeSymbols.Add(typeSymbol);

                    // Also add nested types
                    foreach (var nestedType in typeSymbol.GetTypeMembers())
                    {
                        typeSymbols.Add(nestedType);
                    }
                }
                else if (member is INamespaceSymbol nestedNamespace)
                {
                    AddTypesFromNamespace(nestedNamespace, typeSymbols);
                }
            }
        }
    }
}