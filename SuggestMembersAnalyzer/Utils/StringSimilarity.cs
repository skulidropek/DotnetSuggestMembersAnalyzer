// <copyright file="StringSimilarity.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    /// <summary>
    /// Provides methods to calculate string similarity for member name suggestions.
    /// </summary>
    internal static class StringSimilarity
    {
        /// <summary>
        /// Compiled regex for splitting identifiers by camelCase, underscores, spaces, or digits.
        /// </summary>
        private static readonly Regex SplitRegex = new Regex(@"(?=[A-Z])|[_\s\d]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

        /// <summary>
        /// Compiled regex for normalizing strings by removing underscores and spaces.
        /// </summary>
        private static readonly Regex NormalizeRegex = new Regex(@"[_\s]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

        /// <summary>
        /// Computes Jaro similarity between two strings.
        /// </summary>
        /// <param name="s1">First string.</param>
        /// <param name="s2">Second string.</param>
        /// <returns>Jaro similarity score between 0.0 and 1.0.</returns>
        internal static double Jaro(string s1, string s2)
        {
            if (string.Equals(s1, s2, StringComparison.Ordinal))
            {
                return 1.0;
            }

            int len1 = s1.Length;
            int len2 = s2.Length;

            if (len1 == 0 || len2 == 0)
            {
                return 0.0;
            }

            int matchDistance = (int)Math.Floor(Math.Max(len1, len2) / 2.0) - 1;
            var matchData = JaroSimilarityHelper.FindMatches(s1, s2, matchDistance);

            if (matchData.matches == 0)
            {
                return 0.0;
            }

            int transpositions = JaroSimilarityHelper.CountTranspositions(s1, s2, matchData.s1Matches, matchData.s2Matches);

            return JaroSimilarityHelper.CalculateJaroScore(matchData.matches, transpositions, len1, len2);
        }

        /// <summary>
        /// Computes Jaro-Winkler similarity between two strings.
        /// </summary>
        /// <param name="s1">First string.</param>
        /// <param name="s2">Second string.</param>
        /// <returns>Jaro-Winkler similarity score between 0.0 and 1.0.</returns>
        internal static double JaroWinkler(string s1, string s2)
        {
            double jaroSim = Jaro(s1, s2);

            int prefix = 0;
            int maxPrefix = 4;
            for (int i = 0; i < Math.Min(maxPrefix, Math.Min(s1.Length, s2.Length)); i++)
            {
                if (s1[i] == s2[i])
                {
                    prefix++;
                }
                else
                {
                    break;
                }
            }

            double scalingFactor = 0.1;
            return jaroSim + (prefix * scalingFactor * (1 - jaroSim));
        }

        /// <summary>
        /// Splits an identifier into lowercase tokens using camelCase, underscores, spaces, or digits.
        /// </summary>
        /// <param name="identifier">The identifier to split.</param>
        /// <returns>Array of lowercase tokens.</returns>
        internal static string[] SplitIdentifier(string identifier)
        {
            return SplitRegex.Split(identifier)
                .Select(static s => s.ToLowerInvariant())
                .Where(static s => !string.IsNullOrEmpty(s))
                .ToArray();
        }

        /// <summary>
        /// Normalizes a string for similarity comparison by lowercasing and removing underscores and spaces.
        /// </summary>
        /// <param name="str">String to normalize.</param>
        /// <returns>Normalized string.</returns>
        internal static string Normalize(string str)
        {
            return NormalizeRegex.Replace(str.ToLowerInvariant(), string.Empty);
        }

        /// <summary>
        /// Computes a composite similarity score between the unknown query and a candidate.
        /// </summary>
        /// <param name="unknown">The unknown query string.</param>
        /// <param name="candidate">The candidate string to compare against.</param>
        /// <returns>Composite similarity score.</returns>
        internal static double ComputeCompositeScore(string unknown, string candidate)
        {
            string normQuery = Normalize(unknown);
            string normCandidate = Normalize(candidate);

            // Base similarity from Jaro-Winkler
            double baseSimilarity = JaroWinkler(normQuery, normCandidate);

            // Exact match bonus
            double exactBonus = string.Equals(normQuery, normCandidate, StringComparison.Ordinal) ? 0.3 : 0.0;

            // Containment bonus
            bool contains = normCandidate.Contains(normQuery) || normQuery.Contains(normCandidate);
            double containmentBonus = contains ? 0.2 : 0.0;

            // Token bonus
            var tokensQuery = new HashSet<string>(SplitIdentifier(unknown), StringComparer.Ordinal);
            var tokensCandidate = new HashSet<string>(SplitIdentifier(candidate), StringComparer.Ordinal);
            double tokenBonus = 0.0;
            int tokensMatched = 0;

            foreach (var tq in tokensQuery)
            {
                foreach (var tc in tokensCandidate)
                {
                    if (string.Equals(tq, tc, StringComparison.Ordinal))
                    {
                        tokenBonus += 0.2;
                        tokensMatched++;
                    }
                    else if (tq.StartsWith(tc, StringComparison.Ordinal) || tc.StartsWith(tq, StringComparison.Ordinal))
                    {
                        tokenBonus += 0.1;
                        tokensMatched++;
                    }
                }
            }

            // Extra bonus if at least 2 distinct tokens match
            if (tokensMatched >= 2)
            {
                tokenBonus += 0.2;
            }

            // Length penalty: subtract 0.01 per extra character in candidate
            double lengthPenalty = Math.Max(0, candidate.Length - unknown.Length) * 0.01;

            return baseSimilarity + exactBonus + containmentBonus + tokenBonus - lengthPenalty;
        }

        /// <summary>
        /// Gets a formatted list of members with their signatures and types.
        /// </summary>
        /// <param name="objectType">The type to get members from.</param>
        /// <param name="requestedName">The name being searched for.</param>
        /// <returns>List of formatted member strings.</returns>
        internal static List<string> GetFormattedMembersList(ITypeSymbol objectType, string requestedName)
        {
            const double MinScore = 0.3;

            var result = objectType.GetMembers()
                .Select(member => MemberDisplayFormatter.FormatMember(member, requestedName, objectType))
                .Where(static item => item.score >= MinScore)
                .OrderByDescending(static item => item.score)
                .Take(5)
                .Select(static item => item.displayName)
                .ToList();

            return result;
        }

        /// <summary>
        /// Finds possible exported symbols that match the requested name.
        /// </summary>
        /// <param name="requestedName">The name to search for.</param>
        /// <param name="moduleSymbol">The namespace symbol to search in.</param>
        /// <returns>List of matching exported symbols with scores.</returns>
        internal static List<(string name, double score)> FindPossibleExports(
            string requestedName,
            INamespaceSymbol moduleSymbol)
        {
            if (moduleSymbol == null)
            {
                return new List<(string name, double score)>();
            }

            try
            {
                var exports = moduleSymbol.GetMembers();
                const double MinScore = 0.3;

                // Calculate similarity scores, filter by threshold, sort by score and return top 5
                return exports
                    .Select(exportSymbol => (exportSymbol.Name, ComputeCompositeScore(requestedName, exportSymbol.Name)))
                    .Where(item => item.Item2 > MinScore)
                    .OrderByDescending(item => item.Item2)
                    .Take(5)
                    .Select(item => (item.Name, item.Item2))
                    .ToList();
            }
            catch (Exception ex)
            {
                // Log detailed error information for SuggestMembersAnalyzer
                System.Diagnostics.Debug.WriteLine(
                    $"[SuggestMembersAnalyzer] StringSimilarity.FindPossibleExports failed searching for " +
                    $"'{requestedName}' in namespace '{moduleSymbol?.Name ?? "null"}': {ex}");

                // Return empty list if exports retrieval fails
                // This provides graceful degradation while preserving error information
                return new List<(string name, double score)>();
            }
        }

        /// <summary>
        /// Finds similar symbols from a list of key-value tuples where keys are identifiers for search
        /// and values are the full representations to insert. Allows duplicate keys.
        /// </summary>
        /// <typeparam name="TValue">Type of value to return in results.</typeparam>
        /// <param name="queryName">The name being searched for.</param>
        /// <param name="candidateEntries">List of candidate entries with search key and display value.</param>
        /// <returns>List of matching items with scores.</returns>
        internal static List<(string Name, TValue Value, double Score)> FindSimilarSymbols<TValue>(
            string queryName,
            IEnumerable<(string Key, TValue Value)> candidateEntries)
        {
            const double MIN_SCORE = 0.3;

            // Process the provided candidate entries
            return candidateEntries
                .Select(entry => (
                    Name: entry.Key,
                    Value: entry.Value,
                    Score: ComputeCompositeScore(queryName, entry.Key)))
                .Where(item => !string.IsNullOrEmpty(item.Name) &&
                       item.Score >= MIN_SCORE &&
                       !string.Equals(item.Name, queryName, StringComparison.Ordinal)) // Exclude exact matches to the query, as these likely don't exist
                .OrderByDescending(r => r.Score)
                .Take(5)
                .ToList();
        }

        /// <summary>
        /// Finds similar names from a custom list of candidates (simple string array).
        /// </summary>
        /// <param name="queryName">The name being searched for.</param>
        /// <param name="candidateNames">Array of candidate names to search through.</param>
        /// <returns>List of matching names with similarity scores.</returns>
        internal static List<(string Name, double Score)> FindSimilarLocalSymbols(
            string queryName,
            string[] candidateNames)
        {
            const double MIN_SCORE = 0.3;

            // Process the provided candidate names
            return candidateNames
                .Where(candidate => !string.IsNullOrEmpty(candidate))
                .Select(candidate => (Name: candidate, Score: ComputeCompositeScore(queryName, candidate)))
                .Where(item => item.Score >= MIN_SCORE)
                .OrderByDescending(r => r.Score)
                .Take(5)
                .ToList();
        }
    }
}