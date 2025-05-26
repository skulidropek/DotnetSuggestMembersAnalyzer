// <copyright file="ContextualSymbolSuggester.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    /// <summary>
    /// Provides contextual symbol suggestions with intelligent prioritization.
    /// </summary>
    internal static class ContextualSymbolSuggester
    {
        /// <summary>Maximum number of suggestions to return.</summary>
        private const int MaxSuggestions = 5;

        /// <summary>Minimum similarity threshold for suggestions.</summary>
        private const double MinSimilarityThreshold = 0.3;

        /// <summary>
        /// Gathers symbol suggestions with contextual prioritization.
        /// </summary>
        /// <param name="identifier">The identifier syntax node.</param>
        /// <param name="queryName">The name being searched for.</param>
        /// <param name="semanticModel">The semantic model.</param>
        /// <param name="allTypeNames">All available type names.</param>
        /// <param name="compilation">The compilation context.</param>
        /// <returns>List of prioritized symbol suggestions.</returns>
        internal static List<object> GatherPrioritizedSuggestions(
            IdentifierNameSyntax identifier,
            string queryName,
            SemanticModel semanticModel,
            ImmutableArray<string> allTypeNames,
            Compilation compilation)
        {
            List<PrioritizedSuggestion> suggestions = [];

            // 1. Determine usage context for filtering
            UsageContext usageContext = UsageContextAnalyzer.DetermineUsageContext(identifier);

            // 2. Get current class context
            INamedTypeSymbol? currentClass = GetContainingClass(identifier, semanticModel);

            // 3. Gather symbols by context priority
            IEnumerable<(string Key, object Value)> localScopeSymbols = GetLocalScopeSymbols(identifier, semanticModel);
            IEnumerable<(string Key, object Value)> currentClassSymbols = GetCurrentClassSymbols(currentClass);
            IEnumerable<(string Key, object Value)> projectSymbols = GetProjectSymbols(semanticModel, identifier, compilation, currentClass);
            IEnumerable<(string Key, object Value)> librarySymbols = GetLibrarySymbols(allTypeNames, compilation);

            // 4. Apply usage context filtering
            localScopeSymbols = UsageContextAnalyzer.FilterSymbolsByContext(localScopeSymbols, usageContext);
            currentClassSymbols = UsageContextAnalyzer.FilterSymbolsByContext(currentClassSymbols, usageContext);
            projectSymbols = UsageContextAnalyzer.FilterSymbolsByContext(projectSymbols, usageContext);
            librarySymbols = UsageContextAnalyzer.FilterSymbolsByContext(librarySymbols, usageContext);

            // 5. Create prioritized suggestions
            suggestions.AddRange(CreateSuggestions(localScopeSymbols, SymbolContext.LocalScope, queryName));
            suggestions.AddRange(CreateSuggestions(currentClassSymbols, SymbolContext.CurrentClass, queryName));
            suggestions.AddRange(CreateSuggestions(projectSymbols, SymbolContext.CurrentProject, queryName));
            suggestions.AddRange(CreateSuggestions(librarySymbols, SymbolContext.ExternalLibrary, queryName));

            // 6. Deduplicate by symbol and sort by final score
            return [.. suggestions
                .Where(s => s.SimilarityScore >= MinSimilarityThreshold)
                .GroupBy(s => GetSymbolKey(s.Symbol), StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(s => s.FinalScore).First())
                .OrderByDescending(s => s.FinalScore)
                .ThenByDescending(s => s.SimilarityScore)
                .Take(MaxSuggestions)
                .Select(s => s.Symbol),];
        }

        /// <summary>
        /// Gets the containing class/struct/interface for the given identifier.
        /// </summary>
        /// <param name="identifier">The identifier syntax.</param>
        /// <param name="semanticModel">The semantic model.</param>
        /// <returns>The containing type symbol, or null if not found.</returns>
        private static INamedTypeSymbol? GetContainingClass(IdentifierNameSyntax identifier, SemanticModel semanticModel)
        {
            // Walk up the syntax tree to find the containing type declaration
            SyntaxNode? current = identifier.Parent;
            while (current is not null)
            {
                if (current is ClassDeclarationSyntax or
                    StructDeclarationSyntax or
                    InterfaceDeclarationSyntax or
                    RecordDeclarationSyntax)
                {
                    return semanticModel.GetDeclaredSymbol(current) as INamedTypeSymbol;
                }

                current = current.Parent;
            }

            return null;
        }

        /// <summary>
        /// Gets symbols from the current class including inherited members.
        /// </summary>
        /// <param name="currentClass">The current class symbol.</param>
        /// <returns>Enumerable of current class symbols including inherited members.</returns>
        private static IEnumerable<(string Key, object Value)> GetCurrentClassSymbols(
            INamedTypeSymbol? currentClass)
        {
            if (currentClass is null)
            {
                yield break;
            }

            // Get all members of the current class (nested types are included in GetMembers)
            IEnumerable<ISymbol> filteredMembers = currentClass.GetMembers()
                .Where(m => m.Kind is SymbolKind.Field or SymbolKind.Property or SymbolKind.Method or
                           SymbolKind.NamedType or SymbolKind.Event);

            foreach (ISymbol member in filteredMembers)
            {
                yield return (member.Name, member);
            }

            // Add inherited members from base class
            INamedTypeSymbol? baseType = currentClass.BaseType;
            while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
            {
                IEnumerable<ISymbol> inheritedMembers = baseType.GetMembers()
                    .Where(m => m.Kind is SymbolKind.Field or SymbolKind.Property or SymbolKind.Method or SymbolKind.Event)
                    .Where(m => m.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal);

                foreach (ISymbol member in inheritedMembers)
                {
                    yield return (member.Name, member);
                }

                baseType = baseType.BaseType;
            }

            // Add members from implemented interfaces
            foreach (INamedTypeSymbol interfaceType in currentClass.AllInterfaces)
            {
                IEnumerable<ISymbol> interfaceMembers = interfaceType.GetMembers()
                    .Where(m => m.Kind is SymbolKind.Property or SymbolKind.Method or SymbolKind.Event);

                foreach (ISymbol member in interfaceMembers)
                {
                    yield return (member.Name, member);
                }
            }
        }

        /// <summary>
        /// Gets symbols from the current project (source assembly).
        /// </summary>
        /// <param name="semanticModel">The semantic model.</param>
        /// <param name="identifier">The identifier syntax.</param>
        /// <param name="compilation">The compilation context.</param>
        /// <param name="currentClass">Current class to exclude its members.</param>
        /// <returns>Enumerable of project symbols.</returns>
        private static IEnumerable<(string Key, object Value)> GetProjectSymbols(
            SemanticModel semanticModel,
            IdentifierNameSyntax identifier,
            Compilation compilation,
            INamedTypeSymbol? currentClass)
        {
            // Get current class members to exclude them
            HashSet<ISymbol> currentClassMembers = currentClass != null
                ? new HashSet<ISymbol>(currentClass.GetMembers(), SymbolEqualityComparer.Default)
                : [];

            // Get visible symbols from the current scope (excluding current class members)
            IEnumerable<ISymbol> visibleSymbols = semanticModel.LookupSymbols(identifier.SpanStart)
                .Where(s => s.Kind is SymbolKind.Local or SymbolKind.Parameter or
                           SymbolKind.Field or SymbolKind.Property or SymbolKind.Method)
                .Where(s => IsFromSourceAssembly(s, compilation) && !currentClassMembers.Contains(s));

            foreach (ISymbol symbol in visibleSymbols)
            {
                yield return (symbol.Name, symbol);
            }

            // Get types from source assembly (excluding current class)
            foreach (INamedTypeSymbol type in GetSourceAssemblyTypeSymbols(compilation))
            {
                // Skip if this is the current class
                if (currentClass != null && SymbolEqualityComparer.Default.Equals(type, currentClass))
                {
                    continue;
                }

                yield return (type.Name, type);
            }
        }

        /// <summary>
        /// Gets symbols from external libraries (referenced assemblies).
        /// </summary>
        /// <param name="allTypeNames">All available type names.</param>
        /// <param name="compilation">The compilation context.</param>
        /// <returns>Enumerable of library symbols.</returns>
        private static IEnumerable<(string Key, object Value)> GetLibrarySymbols(
            ImmutableArray<string> allTypeNames,
            Compilation compilation)
        {
            HashSet<string> sourceAssemblyTypes = [.. GetSourceAssemblyTypes(compilation)];

            foreach (string fullTypeName in allTypeNames.Where(t => !sourceAssemblyTypes.Contains(t)))
            {
                string shortName = ExtractShortTypeName(fullTypeName);

                // Try to get the actual symbol instead of just the string
                INamedTypeSymbol? typeSymbol = compilation.GetTypeByMetadataName(fullTypeName);
                if (typeSymbol != null)
                {
                    yield return (shortName, typeSymbol);
                }
                else
                {
                    // Fallback to string if symbol not found
                    yield return (shortName, fullTypeName);
                }
            }
        }

        /// <summary>
        /// Extracts short type name from full type name, handling generic types.
        /// </summary>
        /// <param name="fullTypeName">Full type name.</param>
        /// <returns>Short type name without namespace and generic parameters.</returns>
        private static string ExtractShortTypeName(string fullTypeName)
        {
            // Extract name after last dot
            int lastDot = fullTypeName.LastIndexOf('.') + 1;
            string nameWithoutNamespace = lastDot > 0 ? fullTypeName.Substring(lastDot) : fullTypeName;

            // Remove generic parameters (everything after '<' or '`')
            int genericStart = nameWithoutNamespace.IndexOfAny(['<', '`']);
            return genericStart >= 0 ? nameWithoutNamespace.Substring(0, genericStart) : nameWithoutNamespace;
        }

        /// <summary>
        /// Creates prioritized suggestions from symbol entries.
        /// </summary>
        /// <param name="symbolEntries">Symbol entries to process.</param>
        /// <param name="context">The context/priority level.</param>
        /// <param name="queryName">The name being searched for.</param>
        /// <returns>List of prioritized suggestions.</returns>
        private static List<PrioritizedSuggestion> CreateSuggestions(
            IEnumerable<(string Key, object Value)> symbolEntries,
            SymbolContext context,
            string queryName)
        {
            return [.. symbolEntries
                .Select(entry => new PrioritizedSuggestion(
                    entry.Value,
                    StringSimilarity.ComputeCompositeScore(queryName, entry.Key),
                    context))
                .Where(s => s.SimilarityScore >= MinSimilarityThreshold),];
        }

        /// <summary>
        /// Determines if a symbol is from the source assembly.
        /// </summary>
        /// <param name="symbol">The symbol to check.</param>
        /// <param name="compilation">The compilation context.</param>
        /// <returns>True if symbol is from source assembly.</returns>
        private static bool IsFromSourceAssembly(ISymbol symbol, Compilation compilation)
        {
            return SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly);
        }

        /// <summary>
        /// Gets all type names from the source assembly.
        /// </summary>
        /// <param name="compilation">The compilation context.</param>
        /// <returns>List of source assembly type names.</returns>
        private static List<string> GetSourceAssemblyTypes(Compilation compilation)
        {
            List<string> result = [];

            void WalkNamespace(INamespaceSymbol ns)
            {
                IEnumerable<INamedTypeSymbol> sourceTypes = ns.GetTypeMembers()
                    .Where(t => SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, compilation.Assembly));

                foreach (INamedTypeSymbol type in sourceTypes)
                {
                    AddTypeWithNested(type);
                }

                foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
                {
                    WalkNamespace(child);
                }
            }

            void AddTypeWithNested(INamedTypeSymbol type)
            {
                result.Add(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty));
                foreach (INamedTypeSymbol nested in type.GetTypeMembers())
                {
                    AddTypeWithNested(nested);
                }
            }

            WalkNamespace(compilation.GlobalNamespace);
            return result;
        }

        /// <summary>
        /// Gets a unique key for a symbol to enable deduplication.
        /// </summary>
        /// <param name="symbol">The symbol or string to get key for.</param>
        /// <returns>Unique string key for the symbol.</returns>
        private static string GetSymbolKey(object symbol)
        {
            return symbol switch
            {
                ISymbol s => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                string str => str,
                _ => symbol?.ToString() ?? string.Empty,
            };
        }

        /// <summary>
        /// Gets symbols from local scope (current method parameters and local variables).
        /// </summary>
        /// <param name="identifier">The identifier syntax.</param>
        /// <param name="semanticModel">The semantic model.</param>
        /// <returns>Enumerable of local scope symbols.</returns>
        private static IEnumerable<(string Key, object Value)> GetLocalScopeSymbols(
            IdentifierNameSyntax identifier,
            SemanticModel semanticModel)
        {
            // Get the containing method/property/constructor/etc.
            SyntaxNode? containingMember = GetContainingMember(identifier);
            if (containingMember is null)
            {
                yield break;
            }

            // Get all symbols visible at the identifier position
            IEnumerable<ISymbol> localSymbols = semanticModel.LookupSymbols(identifier.SpanStart)
                .Where(s => s.Kind is SymbolKind.Local or SymbolKind.Parameter)
                .Where(s => IsSymbolFromContainingMember(s, containingMember, semanticModel));

            foreach (ISymbol symbol in localSymbols)
            {
                yield return (symbol.Name, symbol);
            }
        }

        /// <summary>
        /// Gets the containing member (method, property, constructor, etc.) for an identifier.
        /// </summary>
        /// <param name="identifier">The identifier syntax.</param>
        /// <returns>The containing member syntax node.</returns>
        private static SyntaxNode? GetContainingMember(IdentifierNameSyntax identifier)
        {
            SyntaxNode? current = identifier.Parent;
            while (current != null)
            {
                if (current is MethodDeclarationSyntax or
                    ConstructorDeclarationSyntax or
                    PropertyDeclarationSyntax or
                    AccessorDeclarationSyntax or
                    LocalFunctionStatementSyntax or
                    AnonymousMethodExpressionSyntax or
                    SimpleLambdaExpressionSyntax or
                    ParenthesizedLambdaExpressionSyntax)
                {
                    return current;
                }

                current = current.Parent;
            }

            return null;
        }

        /// <summary>
        /// Determines if a symbol belongs to the specified containing member.
        /// </summary>
        /// <param name="symbol">The symbol to check.</param>
        /// <param name="containingMember">The containing member syntax.</param>
        /// <param name="semanticModel">The semantic model.</param>
        /// <returns>True if the symbol is from the containing member.</returns>
        private static bool IsSymbolFromContainingMember(
            ISymbol symbol,
            SyntaxNode containingMember,
            SemanticModel semanticModel)
        {
            // For parameters, check if they belong to the current method/constructor/etc.
            if (symbol is IParameterSymbol parameter)
            {
                ISymbol? containingMethodSymbol = semanticModel.GetDeclaredSymbol(containingMember);
                return containingMethodSymbol switch
                {
                    IMethodSymbol method => method.Parameters.Contains(parameter),
                    IPropertySymbol property => property.Parameters.Contains(parameter),
                    _ => false,
                };
            }

            // For local variables, check if they are declared within the current member
            if (symbol is ILocalSymbol local)
            {
                // Get the syntax reference for the local symbol
                foreach (SyntaxReference syntaxRef in local.DeclaringSyntaxReferences)
                {
                    SyntaxNode localDeclaration = syntaxRef.GetSyntax();

                    // Check if the local declaration is within the containing member
                    if (localDeclaration.Ancestors().Any(ancestor => ancestor == containingMember))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Gets all type symbols from the source assembly.
        /// </summary>
        /// <param name="compilation">The compilation context.</param>
        /// <returns>List of source assembly type symbols.</returns>
        private static List<INamedTypeSymbol> GetSourceAssemblyTypeSymbols(Compilation compilation)
        {
            List<INamedTypeSymbol> result = [];

            void WalkNamespace(INamespaceSymbol ns)
            {
                IEnumerable<INamedTypeSymbol> sourceTypes = ns.GetTypeMembers()
                    .Where(t => SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, compilation.Assembly));

                foreach (INamedTypeSymbol type in sourceTypes)
                {
                    AddTypeWithNested(type);
                }

                foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
                {
                    WalkNamespace(child);
                }
            }

            void AddTypeWithNested(INamedTypeSymbol type)
            {
                result.Add(type);
                foreach (INamedTypeSymbol nested in type.GetTypeMembers())
                {
                    AddTypeWithNested(nested);
                }
            }

            WalkNamespace(compilation.GlobalNamespace);
            return result;
        }
    }
}