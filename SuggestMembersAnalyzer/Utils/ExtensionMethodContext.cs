// <copyright file="ExtensionMethodContext.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    using System.Collections.Generic;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;

    /// <summary>
    /// Context for extension method discovery to reduce parameter passing.
    /// </summary>
    internal sealed class ExtensionMethodContext
    {
        private readonly Compilation compilation;
        private readonly List<(string Name, ISymbol Symbol)> entries;
        private readonly ITypeSymbol receiverType;
        private readonly HashSet<string> seenNames;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExtensionMethodContext"/> class.
        /// </summary>
        /// <param name="receiverType">The receiver type for extension methods.</param>
        /// <param name="compilation">The compilation context.</param>
        /// <param name="seenNames">Set of already seen method names.</param>
        /// <param name="entries">List to add discovered methods to.</param>
        internal ExtensionMethodContext(
            ITypeSymbol receiverType,
            Compilation compilation,
            HashSet<string> seenNames,
            List<(string Name, ISymbol Symbol)> entries)
        {
            this.receiverType = receiverType ?? throw new System.ArgumentNullException(nameof(receiverType));
            this.compilation = compilation ?? throw new System.ArgumentNullException(nameof(compilation));
            this.seenNames = seenNames ?? throw new System.ArgumentNullException(nameof(seenNames));
            this.entries = entries ?? throw new System.ArgumentNullException(nameof(entries));
        }

        /// <summary>
        /// Tries to add an extension method to the results.
        /// </summary>
        /// <param name="method">The method to try adding.</param>
        internal void TryAdd(IMethodSymbol method)
        {
            if (method is null)
            {
                return;
            }

            IMethodSymbol? candidate = method.MethodKind == MethodKind.ReducedExtension ? method : method.IsExtensionMethod ? BindExtension(method) : null;

            if (candidate is null || !seenNames.Add(candidate.Name))
            {
                return;
            }

            entries.Add((candidate.Name, candidate));
        }

        /// <summary>
        /// Attempts to bind an extension method to the receiver type.
        /// </summary>
        /// <param name="ext">The extension method to bind.</param>
        /// <returns>The bound method symbol, or null if binding fails.</returns>
        private IMethodSymbol? BindExtension(IMethodSymbol ext)
        {
            if (ext is null)
            {
                return null;
            }

            IMethodSymbol? reduced = ext.ReduceExtensionMethod(receiverType);
            if (reduced is not null)
            {
                return reduced;
            }

            if (ext.Parameters.Length == 0)
            {
                return null;
            }

            ITypeSymbol thisParam = ext.Parameters[0].Type;
            Conversion conv = compilation.ClassifyConversion(receiverType, thisParam);
            return (conv is { Exists: true, IsExplicit: false }) ? ext : null;
        }
    }
}