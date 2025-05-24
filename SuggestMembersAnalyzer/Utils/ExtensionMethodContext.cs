// <copyright file="ExtensionMethodContext.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    using System;
    using System.Collections.Generic;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;

    /// <summary>
    /// Context for extension method discovery to reduce parameter passing.
    /// </summary>
    internal sealed class ExtensionMethodContext
    {
        private readonly ITypeSymbol receiverType;
        private readonly Compilation compilation;
        private readonly HashSet<string> seenNames;
        private readonly List<(string Name, ISymbol Symbol)> entries;

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
            this.receiverType = receiverType;
            this.compilation = compilation;
            this.seenNames = seenNames;
            this.entries = entries;
        }

        /// <summary>
        /// Tries to add an extension method to the results.
        /// </summary>
        /// <param name="method">The method to try adding.</param>
        internal void TryAdd(IMethodSymbol method)
        {
            IMethodSymbol? candidate;

            if (method.MethodKind == MethodKind.ReducedExtension)
            {
                candidate = method;
            }
            else if (method.IsExtensionMethod)
            {
                candidate = this.BindExtension(method);
            }
            else
            {
                candidate = null;
            }

            if (candidate is null || !this.seenNames.Add(candidate.Name))
            {
                return;
            }

            this.entries.Add((candidate.Name, candidate));
        }

        /// <summary>
        /// Attempts to bind an extension method to the receiver type.
        /// </summary>
        /// <param name="ext">The extension method to bind.</param>
        /// <returns>The bound method symbol, or null if binding fails.</returns>
        private IMethodSymbol? BindExtension(IMethodSymbol ext)
        {
            var reduced = ext.ReduceExtensionMethod(this.receiverType);
            if (reduced is not null)
            {
                return reduced;
            }

            if (ext.Parameters.Length == 0)
            {
                return null;
            }

            var thisParam = ext.Parameters[0].Type;
            var conv = this.compilation.ClassifyConversion(this.receiverType, thisParam);
            return (conv.Exists && !conv.IsExplicit) ? ext : null;
        }
    }
}