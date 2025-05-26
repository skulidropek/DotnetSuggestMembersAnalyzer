// <copyright file="PrioritizedSuggestion.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    /// <summary>
    /// Represents a symbol suggestion with contextual priority information.
    /// </summary>
    internal sealed class PrioritizedSuggestion
    {
        /// <summary>
        /// Context bonus values for different symbol origins.
        /// </summary>
        private const double LocalScopeBonus = 0.3;
        private const double CurrentClassBonus = 0.2;
        private const double CurrentProjectBonus = 0.1;
        private const double ExternalLibraryBonus = 0.0;

        /// <summary>
        /// Additional bonus for common .NET types when similarity is very high.
        /// </summary>
        private const double CommonNetTypeBonus = 0.25;
        private const double HighSimilarityThreshold = 0.8;

        /// <summary>
        /// Initializes a new instance of the <see cref="PrioritizedSuggestion"/> class.
        /// </summary>
        /// <param name="symbol">The suggested symbol.</param>
        /// <param name="similarityScore">String similarity score.</param>
        /// <param name="context">Context/priority level.</param>
        internal PrioritizedSuggestion(object symbol, double similarityScore, SymbolContext context)
        {
            Symbol = symbol;
            SimilarityScore = similarityScore;
            Context = context;
        }

        /// <summary>Gets the suggested symbol.</summary>
        internal object Symbol { get; }

        /// <summary>Gets the string similarity score.</summary>
        internal double SimilarityScore { get; }

        /// <summary>Gets the symbol context.</summary>
        internal SymbolContext Context { get; }

        /// <summary>Gets the final score combining similarity and context bonus.</summary>
        internal double FinalScore => SimilarityScore + GetContextBonus() + GetCommonTypeBonus();

        /// <summary>
        /// Extracts type name from full type name string.
        /// </summary>
        /// <param name="fullTypeName">Full type name.</param>
        /// <returns>Short type name.</returns>
        private static string ExtractTypeName(string fullTypeName)
        {
            int lastDot = fullTypeName.LastIndexOf('.') + 1;
            return lastDot > 0 ? fullTypeName.Substring(lastDot) : fullTypeName;
        }

        /// <summary>
        /// Determines if a type name is a common .NET type.
        /// </summary>
        /// <param name="typeName">Type name to check.</param>
        /// <returns>True if it's a common .NET type.</returns>
        private static bool IsCommonNetType(string typeName)
        {
            return typeName switch
            {
                "Dictionary" or "List" or "Array" or "String" or "StringBuilder" or
                "HashSet" or "Queue" or "Stack" or "ConcurrentDictionary" or
                "IEnumerable" or "ICollection" or "IList" or "IDictionary" or
                "Task" or "DateTime" or "TimeSpan" or "Guid" => true,
                _ => false,
            };
        }

        /// <summary>
        /// Gets the context bonus based on symbol origin.
        /// </summary>
        /// <returns>Context bonus value.</returns>
        /// <exception cref="System.NotSupportedException">Thrown when context is Unknown.</exception>
        private double GetContextBonus()
        {
            return Context switch
            {
                SymbolContext.LocalScope => LocalScopeBonus,
                SymbolContext.CurrentClass => CurrentClassBonus,
                SymbolContext.CurrentProject => CurrentProjectBonus,
                SymbolContext.ExternalLibrary => ExternalLibraryBonus,
                SymbolContext.Unknown => throw new System.NotSupportedException("Unknown context is not supported"),
                _ => ExternalLibraryBonus,
            };
        }

        /// <summary>
        /// Gets additional bonus for common .NET types with high similarity.
        /// </summary>
        /// <returns>Common type bonus value.</returns>
        private double GetCommonTypeBonus()
        {
            if (SimilarityScore < HighSimilarityThreshold)
            {
                return 0.0;
            }

            string symbolName = GetSymbolName();
            return IsCommonNetType(symbolName) ? CommonNetTypeBonus : 0.0;
        }

        /// <summary>
        /// Gets the symbol name for comparison.
        /// </summary>
        /// <returns>Symbol name.</returns>
        private string GetSymbolName()
        {
            return Symbol switch
            {
                Microsoft.CodeAnalysis.ISymbol s => s.Name,
                string str => ExtractTypeName(str),
                _ => Symbol?.ToString() ?? string.Empty,
            };
        }
    }
}