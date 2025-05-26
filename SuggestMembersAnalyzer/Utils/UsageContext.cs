// <copyright file="UsageContext.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    /// <summary>
    /// Defines the usage context of an identifier to filter appropriate suggestions.
    /// </summary>
    internal enum UsageContext
    {
        /// <summary>Unknown usage context.</summary>
        Unknown = 0,

        /// <summary>Identifier is used as a type (e.g., variable declaration, parameter type, cast).</summary>
        TypeUsage = 1,

        /// <summary>Identifier is used as a value/expression (e.g., method call, field access).</summary>
        ValueUsage = 2,

        /// <summary>Identifier is used in attribute context.</summary>
        AttributeUsage = 3,

        /// <summary>Identifier is used in namespace context.</summary>
        NamespaceUsage = 4,
    }
}