// <copyright file="MemberDisplayFormatter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    using System;
    using System.Linq;
    using Microsoft.CodeAnalysis;

    /// <summary>
    /// Helper for formatting member display names to reduce method complexity.
    /// </summary>
    internal static class MemberDisplayFormatter
    {
        /// <summary>
        /// Formats a member symbol into a display name with signature information.
        /// </summary>
        /// <param name="member">The member symbol to format.</param>
        /// <param name="requestedName">The requested name for similarity scoring.</param>
        /// <param name="objectType">The containing type for error context.</param>
        /// <returns>Tuple of member name, display name, and similarity score.</returns>
        internal static (string name, string displayName, double score) FormatMember(
            ISymbol member, string requestedName, ITypeSymbol objectType)
        {
            if (member is null)
            {
                return (string.Empty, string.Empty, 0.0);
            }

            if (string.IsNullOrEmpty(requestedName))
            {
                return (member.Name, member.Name, 0.0);
            }

            try
            {
                string displayName = FormatMemberDisplayName(member);
                double score = StringSimilarity.ComputeCompositeScore(requestedName, member.Name);
                return (member.Name, displayName, score);
            }
            catch (Exception ex)
            {
                // Log detailed error information for SuggestMembersAnalyzer
                Console.WriteLine(
                    "[SuggestMembersAnalyzer] MemberDisplayFormatter.FormatMember failed processing " +
                    $"member '{member?.Name ?? "null"}' of type '{member?.Kind}' in '{objectType?.Name ?? "null"}': {ex}");

                // In case of error, add just the member name
                string memberName = member?.Name ?? "unknown";
                double score = StringSimilarity.ComputeCompositeScore(requestedName, memberName);
                return (memberName, memberName, score);
            }
        }

        /// <summary>
        /// Formats the display name based on member type.
        /// </summary>
        /// <param name="member">The member symbol to format.</param>
        /// <returns>Formatted display name.</returns>
        private static string FormatMemberDisplayName(ISymbol member)
        {
            return member is null
                ? string.Empty
                : member switch
                {
                    IMethodSymbol methodSymbol => FormatMethodDisplayName(methodSymbol),
                    IPropertySymbol propertySymbol => $"{member.Name}: {propertySymbol.Type?.ToString() ?? "object"}",
                    IFieldSymbol fieldSymbol => $"{member.Name}: {fieldSymbol.Type?.ToString() ?? "object"}",
                    _ => member.Name,
                };
        }

        /// <summary>
        /// Formats method display name with parameters and return type.
        /// </summary>
        /// <param name="methodSymbol">The method symbol to format.</param>
        /// <returns>Formatted method display name.</returns>
        private static string FormatMethodDisplayName(IMethodSymbol methodSymbol)
        {
            if (methodSymbol is null)
            {
                return string.Empty;
            }

            System.Collections.Generic.List<string> parameters = [.. methodSymbol.Parameters
                .Select(static p => $"{p.Name ?? "param"}: {p.Type?.ToString() ?? "object"}"),];

            string paramString = string.Join(", ", parameters);
            string returnType = methodSymbol.ReturnType?.ToString() ?? "void";

            string displayName = $"{methodSymbol.Name}({paramString})";
            if (!string.Equals(returnType, "void", StringComparison.Ordinal))
            {
                displayName += $": {returnType}";
            }

            return displayName;
        }
    }
}