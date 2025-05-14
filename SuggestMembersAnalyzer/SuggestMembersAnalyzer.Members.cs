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
    // Part of the analyzer responsible for checking class member access (e.g., player.Health1)
    public partial class SuggestMembersAnalyzer
    {
        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            var memberAccess = (MemberAccessExpressionSyntax)context.Node;
            var memberName = memberAccess.Name.Identifier.Text;

            // Determine if this is a method call (e.g., items.count()) or property/field access (e.g., items.count)
            bool isMethodCall = memberAccess.Parent is InvocationExpressionSyntax invocation &&
                                invocation.Expression == memberAccess;
            // Skip if this is part of an attribute
            if (memberAccess.Parent is AttributeSyntax)
            {
                return;
            }

            // Get the semantic model to check if the member exists
            var semanticModel = context.SemanticModel;
            var symbolInfo = semanticModel.GetSymbolInfo(memberAccess.Name);

            // If we found a symbol, it means the member exists
            if (symbolInfo.Symbol != null)
            {
                return;
            }

            // Skip if the issue is with overload resolution (the member exists but with wrong arguments)
            // This prevents false positives on methods like 
            // but might be called with invalid arguments
            if (symbolInfo.CandidateReason == CandidateReason.OverloadResolutionFailure)
            {
                return;
            }

            // Check if we have candidate symbols - if so, the method probably exists but has some other issue
            if (symbolInfo.CandidateSymbols.Length > 0)
            {
                bool hasMethodCandidates = symbolInfo.CandidateSymbols.Any(s => s.Kind == SymbolKind.Method);

                // If we have method candidates, it means the method exists but there's some other issue,
                // likely with arguments - skip in this case
                if (hasMethodCandidates)
                {
                    return;
                }
            }


            // Special case for extension methods: they might not be found but still accessible
            if (isMethodCall && symbolInfo.CandidateReason == CandidateReason.MemberGroup)
            {
                // We still proceed in this case as it could be a misspelled extension method
            }

            // Fix: Replace UnboundGenericName (which doesn't exist) with a specific check for SymbolKind.TypeParameter
            else if (symbolInfo.CandidateReason == CandidateReason.LateBound)
            {
                return;
            }

            // Try to get the type of the expression to find similar members
            var expressionTypeInfo = semanticModel.GetTypeInfo(memberAccess.Expression);
            var expressionType = expressionTypeInfo.Type;

            // Also try to use ConvertedType if Type is null
            expressionType ??= expressionTypeInfo.ConvertedType;

            if (expressionType == null)
            {
                // Try getting the type through symbol info
                var expressionSymbolInfo = semanticModel.GetSymbolInfo(memberAccess.Expression);
                if (expressionSymbolInfo.Symbol != null &&
                    (expressionSymbolInfo.Symbol.Kind == SymbolKind.Local ||
                     expressionSymbolInfo.Symbol.Kind == SymbolKind.Field ||
                     expressionSymbolInfo.Symbol.Kind == SymbolKind.Property))
                {
                    var symbolType = GetTypeFromSymbol(expressionSymbolInfo.Symbol);
                    if (symbolType != null)
                    {
                        expressionType = symbolType;
                    }
                }
            }

            // Still no type - give up
            if (expressionType == null)
            {
                return;
            }

            // Find similar members in the type
            var similarMembers = GetSimilarMembers(expressionType, memberName, isMethodCall);

            if (similarMembers.Count > 0)
            {
                // Format suggestions
                var suggestions = similarMembers.Select(m => GetFormattedMemberRepresentation(m, true));
                var suggestionsText = "\n- " + string.Join("\n- ", suggestions);

                // Create a properties dictionary for the code fix provider
                var properties = new Dictionary<string, string?>
                {
                    { "Suggestions", string.Join("|", similarMembers.Select(m => m.Name)) }
                }.ToImmutableDictionary();

                // Check if there are any suggestions
                if (suggestionsText.Length == 0)
                {
                    return;
                }

                // Create and report the diagnostic
                var diagnostic = Diagnostic.Create(
                    MemberNotFoundRule,
                    memberAccess.Name.GetLocation(),
                    properties,
                    memberName,
                    expressionType.Name,
                    suggestionsText);

                context.ReportDiagnostic(diagnostic);
            }
        }

        private static List<ISymbol> GetSimilarMembers(ITypeSymbol type, string memberName, bool isMethodCall)
        {
            // Get all members of the type including those from base types and interfaces
            var allMembers = new List<ISymbol>();

            // Get directly declared members
            allMembers.AddRange(type.GetMembers()
                .Where(m => !m.IsImplicitlyDeclared)
                .Where(m => m.DeclaredAccessibility == Accessibility.Public) // Only include public members
                .Where(m => m.Name != memberName)); // Exclude only exact match

            // Get members from base types
            var baseType = type.BaseType;
            while (baseType != null)
            {
                allMembers.AddRange(baseType.GetMembers()
                    .Where(m => !m.IsImplicitlyDeclared)
                    .Where(m => m.DeclaredAccessibility == Accessibility.Public)
                    .Where(m => m.Name != memberName));
                baseType = baseType.BaseType;
            }

            // Get members from interfaces
            foreach (var iface in type.AllInterfaces)
            {
                allMembers.AddRange(iface.GetMembers()
                    .Where(m => !m.IsImplicitlyDeclared)
                    .Where(m => m.DeclaredAccessibility == Accessibility.Public)
                    .Where(m => m.Name != memberName));
            }

            // If this is a method call, prioritize methods
            // If not, prioritize properties/fields
            List<ISymbol> symbolsInOrder;

            // Find members with names similar to the requested one
            var similarNamedMembers = allMembers
                .Where(m =>
                {
                    // Calculate name similarity
                    double similarity = Utils.StringSimilarity.ComputeCompositeScore(memberName, m.Name);
                    // If names are similar or one starts with the other
                    return similarity > 0.7 ||
                           m.Name.StartsWith(memberName, StringComparison.OrdinalIgnoreCase) ||
                           memberName.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (isMethodCall)
            {
                // Prioritize methods first - especially for common misnamed methods like "count" -> "Count()"
                symbolsInOrder = [.. allMembers
                    .OrderBy(m =>
                    {
                        // Names starting with the requested name or vice versa have highest priority
                        if (m.Name.StartsWith(memberName, StringComparison.OrdinalIgnoreCase) ||
                            memberName.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            return -1; // Highest priority
                        }
                        // Property accessors (get_X, set_X) have low priority
                        else if (m is IMethodSymbol methodSymbol &&
                            (methodSymbol.MethodKind == MethodKind.PropertyGet ||
                             methodSymbol.MethodKind == MethodKind.PropertySet))
                        {
                            return 2; // Low priority
                        }
                        // Regular methods have high priority
                        else if (m.Kind == SymbolKind.Method)
                        {
                            return 0; // High priority
                        }
                        // Properties have medium priority
                        else if (m.Kind == SymbolKind.Property)
                        {
                            return 1; // Medium priority
                        }
                        // Other symbols have low priority
                        else { return 3; }
                    })
                    .ThenBy(m => 1 - Utils.StringSimilarity.ComputeCompositeScore(memberName, m.Name))];
            }
            else
            {
                // Prioritize properties/fields for property/field access
                symbolsInOrder = [.. allMembers
                    .OrderBy(m =>
                    {
                        // Names starting with the requested name or vice versa have highest priority
                        if (m.Name.StartsWith(memberName, StringComparison.OrdinalIgnoreCase) ||
                            memberName.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            return -1; // Highest priority
                        }
                        // Properties have highest priority
                        else if (m.Kind == SymbolKind.Property)
                        {
                            return 0; // High priority
                        }
                        // Property accessors (get_X, set_X) have low priority
                        else if (m is IMethodSymbol methodSymbol &&
                                (methodSymbol.MethodKind == MethodKind.PropertyGet ||
                                 methodSymbol.MethodKind == MethodKind.PropertySet))
                        {
                            return 3; // Low priority
                        }
                        // Regular methods have low priority
                        else if (m.Kind == SymbolKind.Method)
                        {
                            return 2; // Low priority
                        }
                        // Fields have medium priority
                        else { return 1; }
                    })
                    .ThenBy(m => 1 - Utils.StringSimilarity.ComputeCompositeScore(memberName, m.Name))];
            }

            // Add similarly named members to the beginning of the list for prioritization
            if (similarNamedMembers.Count > 0)
            {
                // Add similar members to the beginning (while preserving their order)
                symbolsInOrder = [.. similarNamedMembers
,
                    .. symbolsInOrder.Where(m => !similarNamedMembers.Contains(m))];
            }

            // Take top similar members
            List<ISymbol> similar = [.. symbolsInOrder.Take(5)];

            // Ensure we have actual results
            if (similar.Count == 0)
            {
                // Fall back to string similarity only if no results were found
                similar = [.. allMembers
                    .OrderBy(m => 1 - Utils.StringSimilarity.ComputeCompositeScore(memberName, m.Name))
                    .Take(5)];
            }

            // Remove duplicate properties with the same name, keeping only the best representations
            var uniqueMembers = new List<ISymbol>();
            var addedNames = new HashSet<string>();

            // Preserve order but exclude duplicates
            foreach (var member in similar)
            {
                // For property accessors use property name instead of get_X/set_X
                string nameForComparison;
                if (member is IMethodSymbol methodSymbol &&
                    (methodSymbol.MethodKind == MethodKind.PropertyGet || methodSymbol.MethodKind == MethodKind.PropertySet))
                {
                    // For get_Count use Count
                    if (methodSymbol.Name.StartsWith("get_", StringComparison.Ordinal))
                    {
                        nameForComparison = methodSymbol.Name.Substring(4);
                    }
                    else if (methodSymbol.Name.StartsWith("set_", StringComparison.Ordinal))
                    {
                        nameForComparison = methodSymbol.Name.Substring(4);
                    }
                    else
                    {
                        nameForComparison = methodSymbol.Name;
                    }
                }
                else
                {
                    nameForComparison = member.Name;
                }

                // If we haven't added a member with this name yet, add it
                if (!addedNames.Contains(nameForComparison))
                {
                    uniqueMembers.Add(member);
                    addedNames.Add(nameForComparison);
                }
            }

            return uniqueMembers;
        }

        // Helper method to get a full method signature including parameter names and return type
        private static string GetMethodSignature(IMethodSymbol method)
        {
            try
            {
                // Check if the method is a property accessor
                if (method.MethodKind == MethodKind.PropertyGet && method.Name.StartsWith("get_", StringComparison.Ordinal))
                {
                    // For get_Property methods, convert to property format
                    string propertyName = method.Name.Substring(4); // Remove "get_" prefix

                    // Look for the corresponding property in the containing type for more accurate information
                    var containingType = method.ContainingType;
                    var propertySymbol = containingType.GetMembers(propertyName).OfType<IPropertySymbol>().FirstOrDefault();

                    if (propertySymbol != null)
                    {
                        return GetPropertySignature(propertySymbol);
                    }

                    return $"{GetFormattedTypeName(method.ReturnType)} {propertyName} {{ get; }}";
                }
                else if (method.MethodKind == MethodKind.PropertySet && method.Name.StartsWith("set_", StringComparison.Ordinal))
                {
                    // For set_Property methods, convert to property format
                    string propertyName = method.Name.Substring(4); // Remove "set_" prefix

                    // Look for the corresponding property in the containing type for more accurate information
                    var containingType = method.ContainingType;
                    var propertySymbol = containingType.GetMembers(propertyName).OfType<IPropertySymbol>().FirstOrDefault();

                    if (propertySymbol != null)
                    {
                        return GetPropertySignature(propertySymbol);
                    }

                    // The first parameter's type is the property type
                    var propertyType = method.Parameters.Length > 0 ? method.Parameters[0].Type : method.ContainingType;

                    return $"{GetFormattedTypeName(propertyType)} {propertyName} {{ set; }}";
                }

                StringBuilder signature = new StringBuilder();

                // Add return type
                string returnTypeName = GetFormattedTypeName(method.ReturnType);
                signature.Append(returnTypeName);
                signature.Append(' ');

                // Add method name
                signature.Append(method.Name);

                // Add generic type parameters if any
                if (method.IsGenericMethod && method.TypeParameters.Length > 0)
                {
                    var typeParams = string.Join(", ", method.TypeParameters.Select(tp => tp.Name));
                    signature.Append($"<{typeParams}>");
                }

                // Add parameters
                signature.Append('(');

                if (method.Parameters.Length > 0)
                {
                    var parameters = new List<string>();

                    foreach (var param in method.Parameters)
                    {
                        try
                        {
                            // Add parameter modifiers if present
                            string modifier = "";
                            if (param.RefKind == RefKind.Ref)
                            {
                                modifier = "ref ";
                            }
                            else if (param.RefKind == RefKind.Out)
                            {
                                modifier = "out ";
                            }
                            else if (param.RefKind == RefKind.In)
                            {
                                modifier = "in ";
                            }

                            // Get full type name including namespace for clarity
                            string typeName = GetFormattedTypeName(param.Type);

                            // Add parameter type and name
                            parameters.Add($"{modifier}{typeName} {param.Name}");
                        }
                        catch
                        {
                            parameters.Add("?");
                        }
                    }

                    signature.Append(string.Join(", ", parameters));
                }

                signature.Append(')');

                return signature.ToString();
            }
            catch (Exception)
            {
                // Fallback to basic signature
                return method.Name + "()";
            }
        }

        // Helper method to get a formatted property representation
        private static string GetPropertySignature(IPropertySymbol property)
        {
            try
            {
                StringBuilder signature = new StringBuilder();

                // Add modifiers
                if (property.IsStatic)
                {
                    signature.Append("static ");
                }

                if (property.IsAbstract)
                {
                    signature.Append("abstract ");
                }

                if (property.IsVirtual)
                {
                    signature.Append("virtual ");
                }

                if (property.IsOverride)
                {
                    signature.Append("override ");
                }

                // Add property type
                signature.Append(GetFormattedTypeName(property.Type));
                signature.Append(' ');

                // Add property name
                signature.Append(property.Name);

                // Add accessors
                signature.Append(' ');
                signature.Append('{');
                signature.Append(' ');

                if (property.GetMethod != null)
                {
                    signature.Append("get; ");
                }

                if (property.SetMethod != null)
                {
                    signature.Append("set; ");
                }

                signature.Append('}');

                return signature.ToString();
            }
            catch
            {
                // Fallback to basic signature
                return property.Name;
            }
        }

        // Helper method to get a formatted member representation (with signature for methods)
        private static string GetFormattedMemberRepresentation(ISymbol member, bool includeSignature)
        {
            if (!includeSignature)
            {
                return member.Name;
            }

            try
            {
                if (member is IMethodSymbol methodSymbol)
                {
                    return GetMethodSignature(methodSymbol);
                }
                else if (member is IPropertySymbol propertySymbol)
                {
                    return GetPropertySignature(propertySymbol);
                }
                else if (member is IFieldSymbol fieldSymbol)
                {
                    string modifier = fieldSymbol.IsStatic ? "static " : "";
                    return $"{modifier}{GetFormattedTypeName(fieldSymbol.Type)} {fieldSymbol.Name}";
                }

                return member.Name;
            }
            catch (Exception)
            {
                return member.Name;
            }
        }

        private static List<string> GetTypeMembers(ITypeSymbol type)
        {
            var result = new List<string>();

            // Get all instance members
            foreach (var member in type.GetMembers())
            {
                // Include more member types, filtering fewer special cases
                // Skip only special members like operators
                if (member.IsImplicitlyDeclared || member.Kind == SymbolKind.NamedType)
                {
                    continue;
                }

                // Add the member name
                result.Add(member.Name);

                // Special case for property/method confusion: 
                // If we have a property "Count", also add "count" to detect casing issues
                if ((member.Kind == SymbolKind.Property || member.Kind == SymbolKind.Method) &&
                    member.Name.Length > 0 && char.IsUpper(member.Name[0]))
                {
                    string lowercaseVersion = char.ToLowerInvariant(member.Name[0]) + member.Name.Substring(1);
                    // Only add if not already in the list
                    if (!result.Contains(lowercaseVersion))
                    {
                        result.Add(lowercaseVersion);
                    }
                }
            }

            // Also get members from base types
            if (type.BaseType != null)
            {
                result.AddRange(GetTypeMembers(type.BaseType));
            }

            // Also get members from interfaces
            foreach (var iface in type.AllInterfaces)
            {
                result.AddRange(GetTypeMembers(iface));
            }

            return [.. result.Distinct()];
        }
    }
}