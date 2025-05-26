// <copyright file="UsageContextAnalyzer.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    /// <summary>
    /// Analyzes usage context of identifiers and filters symbols accordingly.
    /// </summary>
    internal static class UsageContextAnalyzer
    {
        /// <summary>
        /// Determines the usage context of an identifier.
        /// </summary>
        /// <param name="identifier">The identifier to analyze.</param>
        /// <returns>The usage context.</returns>
        internal static UsageContext DetermineUsageContext(IdentifierNameSyntax identifier)
        {
            return identifier.Parent switch
            {
                // Type usage contexts
                TypeSyntax => UsageContext.TypeUsage,
                ObjectCreationExpressionSyntax => UsageContext.TypeUsage,
                ParameterSyntax { Type: var t } when t == identifier => UsageContext.TypeUsage,
                VariableDeclarationSyntax { Type: var t } when t == identifier => UsageContext.TypeUsage,
                CastExpressionSyntax { Type: var t } when t == identifier => UsageContext.TypeUsage,
                TypeOfExpressionSyntax { Type: var t } when t == identifier => UsageContext.TypeUsage,
                IsPatternExpressionSyntax { Pattern: DeclarationPatternSyntax { Type: var t } } when t == identifier => UsageContext.TypeUsage,
                BaseTypeSyntax => UsageContext.TypeUsage,
                TypeConstraintSyntax => UsageContext.TypeUsage,
                TypeArgumentListSyntax => UsageContext.TypeUsage,

                // Attribute usage
                AttributeSyntax => UsageContext.AttributeUsage,
                AttributeListSyntax => UsageContext.AttributeUsage,

                // Namespace usage
                UsingDirectiveSyntax => UsageContext.NamespaceUsage,
                NamespaceDeclarationSyntax => UsageContext.NamespaceUsage,
                FileScopedNamespaceDeclarationSyntax => UsageContext.NamespaceUsage,

                // Value/expression usage (method calls, field access, etc.)
                InvocationExpressionSyntax => UsageContext.ValueUsage,
                MemberAccessExpressionSyntax => UsageContext.ValueUsage,
                AssignmentExpressionSyntax => UsageContext.ValueUsage,
                ArgumentSyntax => UsageContext.ValueUsage,
                ReturnStatementSyntax => UsageContext.ValueUsage,
                IfStatementSyntax => UsageContext.ValueUsage,
                WhileStatementSyntax => UsageContext.ValueUsage,
                ForStatementSyntax => UsageContext.ValueUsage,
                BinaryExpressionSyntax => UsageContext.ValueUsage,

                // Default case - check parent context for complex scenarios
                _ => DetermineContextFromParent(identifier),
            };
        }

        /// <summary>
        /// Determines usage context by analyzing parent nodes for complex scenarios.
        /// </summary>
        /// <param name="identifier">The identifier to analyze.</param>
        /// <returns>The usage context determined from parent analysis.</returns>
        internal static UsageContext DetermineContextFromParent(IdentifierNameSyntax identifier)
        {
            // Walk up the syntax tree to find context clues
            SyntaxNode? current = identifier.Parent;
            while (current != null)
            {
                switch (current)
                {
                    // If we're inside a generic type argument list
                    case GenericNameSyntax genericName when IsIdentifierInTypeArgumentList(identifier, genericName):
                        return UsageContext.TypeUsage;

                    // If we're in a type context anywhere up the tree
                    case TypeSyntax:
                    case VariableDeclarationSyntax:
                    case ParameterSyntax:
                    case PropertyDeclarationSyntax:
                    case MethodDeclarationSyntax:
                    case FieldDeclarationSyntax:
                        return UsageContext.TypeUsage;

                    // If we're in a member access, it could be value usage
                    case MemberAccessExpressionSyntax:
                    case InvocationExpressionSyntax:
                        return UsageContext.ValueUsage;

                    default:
                        // Continue searching up the tree
                        break;
                }

                current = current.Parent;
            }

            return UsageContext.Unknown;
        }

        /// <summary>
        /// Checks if an identifier is within the type argument list of a generic name.
        /// </summary>
        /// <param name="identifier">The identifier to check.</param>
        /// <param name="genericName">The generic name syntax.</param>
        /// <returns>True if identifier is in type argument list.</returns>
        internal static bool IsIdentifierInTypeArgumentList(IdentifierNameSyntax identifier, GenericNameSyntax genericName)
        {
            if (genericName.TypeArgumentList is null)
            {
                return false;
            }

            // Check if the identifier is a descendant of the type argument list
            return identifier.Ancestors().Any(ancestor => ancestor == genericName.TypeArgumentList);
        }

        /// <summary>
        /// Filters symbols based on usage context.
        /// </summary>
        /// <param name="symbols">Symbols to filter.</param>
        /// <param name="usageContext">The usage context.</param>
        /// <returns>Filtered symbols appropriate for the context.</returns>
        internal static IEnumerable<(string Key, object Value)> FilterSymbolsByContext(
            IEnumerable<(string Key, object Value)> symbols,
            UsageContext usageContext)
        {
            return usageContext switch
            {
                UsageContext.TypeUsage => FilterForTypeUsage(symbols),
                UsageContext.AttributeUsage => FilterForAttributeUsage(symbols),
                UsageContext.NamespaceUsage => FilterForNamespaceUsage(symbols),
                UsageContext.ValueUsage => FilterForValueUsage(symbols),
                UsageContext.Unknown => symbols, // Don't filter if context is unknown
                _ => symbols,
            };
        }

        /// <summary>
        /// Filters symbols for type usage context (classes, structs, interfaces, enums).
        /// </summary>
        /// <param name="symbols">Symbols to filter.</param>
        /// <returns>Type-related symbols.</returns>
        private static IEnumerable<(string Key, object Value)> FilterForTypeUsage(
            IEnumerable<(string Key, object Value)> symbols)
        {
            return symbols.Where(symbol => symbol.Value switch
            {
                INamedTypeSymbol type => type.TypeKind is TypeKind.Class or TypeKind.Struct or
                                                          TypeKind.Interface or TypeKind.Enum or
                                                          TypeKind.Delegate,
                string => true, // String type names are assumed to be types
                _ => false,
            });
        }

        /// <summary>
        /// Filters symbols for attribute usage context.
        /// </summary>
        /// <param name="symbols">Symbols to filter.</param>
        /// <returns>Attribute-related symbols.</returns>
        private static IEnumerable<(string Key, object Value)> FilterForAttributeUsage(
            IEnumerable<(string Key, object Value)> symbols)
        {
            return symbols.Where(symbol => symbol.Value switch
            {
                INamedTypeSymbol type => IsAttributeType(type),
                string typeName => typeName.EndsWith("Attribute", StringComparison.Ordinal) ||
                                   typeName.IndexOf("Attribute", StringComparison.Ordinal) >= 0,
                _ => false,
            });
        }

        /// <summary>
        /// Filters symbols for namespace usage context.
        /// </summary>
        /// <param name="symbols">Symbols to filter.</param>
        /// <returns>Namespace-related symbols.</returns>
        private static IEnumerable<(string Key, object Value)> FilterForNamespaceUsage(
            IEnumerable<(string Key, object Value)> symbols)
        {
            return symbols.Where(symbol => symbol.Value switch
            {
                INamespaceSymbol => true,
                string namespaceName => namespaceName.IndexOf('.') >= 0, // Likely a namespace
                _ => false,
            });
        }

        /// <summary>
        /// Filters symbols for value usage context (methods, fields, properties, locals, parameters).
        /// </summary>
        /// <param name="symbols">Symbols to filter.</param>
        /// <returns>Value-related symbols.</returns>
        private static IEnumerable<(string Key, object Value)> FilterForValueUsage(
            IEnumerable<(string Key, object Value)> symbols)
        {
            return symbols.Where(symbol => symbol.Value switch
            {
                IMethodSymbol => true,
                IFieldSymbol => true,
                IPropertySymbol => true,
                ILocalSymbol => true,
                IParameterSymbol => true,
                INamedTypeSymbol type => type.TypeKind is TypeKind.Enum or TypeKind.Class or TypeKind.Struct, // Types can be used as values too
                string => true, // String type names should be included
                _ => false,
            });
        }

        /// <summary>
        /// Determines if a type is an attribute type.
        /// </summary>
        /// <param name="type">Type to check.</param>
        /// <returns>True if the type is an attribute.</returns>
        private static bool IsAttributeType(INamedTypeSymbol type)
        {
            // Check if type inherits from System.Attribute
            INamedTypeSymbol? current = type.BaseType;
            while (current != null)
            {
                if (current.Name.Equals("Attribute", StringComparison.Ordinal) &&
                    current.ContainingNamespace?.ToDisplayString().Equals("System", StringComparison.Ordinal) == true)
                {
                    return true;
                }

                current = current.BaseType;
            }

            return false;
        }
    }
}