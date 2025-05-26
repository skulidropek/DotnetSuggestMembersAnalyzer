// <copyright file="SymbolContext.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    /// <summary>
    /// Defines the context/priority level of a symbol suggestion.
    /// </summary>
    internal enum SymbolContext
    {
        /// <summary>Unknown or unspecified context.</summary>
        Unknown = 0,

        /// <summary>Symbol from local scope (parameters, local variables of current method).</summary>
        LocalScope = 1,

        /// <summary>Symbol from the current class/struct/interface.</summary>
        CurrentClass = 2,

        /// <summary>Symbol from the current project (source assembly).</summary>
        CurrentProject = 3,

        /// <summary>Symbol from external libraries (referenced assemblies).</summary>
        ExternalLibrary = 4,
    }
}