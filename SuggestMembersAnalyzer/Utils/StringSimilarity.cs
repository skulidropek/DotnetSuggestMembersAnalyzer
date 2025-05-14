using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace SuggestMembersAnalyzer.Utils
{
    /// <summary>
    /// Represents a match between an input string and a possible correction
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the SimilarityMatch class
    /// </remarks>
    /// <param name="name">The suggested name</param>
    /// <param name="score">The similarity score</param>
    public class SimilarityMatch(string name, double score)
    {
        /// <summary>
        /// Gets the name of the suggested match
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        /// Gets the similarity score between the input and the suggestion
        /// </summary>
        public double Score { get; } = score;
    }

    /// <summary>
    /// Provides methods to calculate string similarity for member name suggestions
    /// </summary>
    internal static class StringSimilarity
    {
        /// <summary>
        /// Computes Jaro similarity between two strings
        /// </summary>
        public static double Jaro(string s1, string s2)
        {
            if (s1 == s2)
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
            bool[] s1Matches = new bool[len1];
            bool[] s2Matches = new bool[len2];
            int matches = 0;
            int transpositions = 0;

            for (int i = 0; i < len1; i++)
            {
                int start = Math.Max(0, i - matchDistance);
                int end = Math.Min(i + matchDistance + 1, len2);

                for (int j = start; j < end; j++)
                {
                    if (s2Matches[j])
                    {
                        continue;
                    }

                    if (s1[i] != s2[j])
                    {
                        continue;
                    }

                    s1Matches[i] = true;
                    s2Matches[j] = true;
                    matches++;
                    break;
                }
            }

            if (matches == 0)
            {
                return 0.0;
            }

            int k = 0;
            for (int i = 0; i < len1; i++)
            {
                if (!s1Matches[i])
                {
                    continue;
                }

                while (!s2Matches[k])
                {
                    k++;
                }

                if (s1[i] != s2[k])
                {
                    transpositions++;
                }

                k++;
            }

            transpositions /= 2;

            return (
                ((double)matches / len1) +
                ((double)matches / len2) +
                (((double)matches - transpositions) / matches)
            ) / 3.0;
        }

        /// <summary>
        /// Computes Jaro-Winkler similarity between two strings
        /// </summary>
        public static double JaroWinkler(string s1, string s2)
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
        /// Splits an identifier into lowercase tokens using camelCase, underscores, spaces, or digits
        /// </summary>
        public static string[] SplitIdentifier(string identifier)
        {
            return [.. Regex.Split(identifier, @"(?=[A-Z])|[_\s\d]")
                .Select(s => s.ToLowerInvariant())
                .Where(s => !string.IsNullOrEmpty(s))];
        }

        /// <summary>
        /// Normalizes a string for similarity comparison by lowercasing and removing underscores and spaces
        /// </summary>
        public static string Normalize(string str)
        {
            return Regex.Replace(str.ToLowerInvariant(), @"[_\s]", "");
        }

        /// <summary>
        /// Computes a composite similarity score between the unknown query and a candidate
        /// </summary>
        public static double ComputeCompositeScore(string unknown, string candidate)
        {
            string normQuery = Normalize(unknown);
            string normCandidate = Normalize(candidate);

            // Base similarity from Jaro-Winkler
            double baseSimilarity = JaroWinkler(normQuery, normCandidate);

            // Exact match bonus
            double exactBonus = (normQuery == normCandidate) ? 0.3 : 0.0;

            // Containment bonus
            bool contains = normCandidate.Contains(normQuery) || normQuery.Contains(normCandidate);
            double containmentBonus = contains ? 0.2 : 0.0;

            // Token bonus
            var tokensQuery = new HashSet<string>(SplitIdentifier(unknown));
            var tokensCandidate = new HashSet<string>(SplitIdentifier(candidate));
            double tokenBonus = 0.0;
            int tokensMatched = 0;

            foreach (var tq in tokensQuery)
            {
                foreach (var tc in tokensCandidate)
                {
                    if (tq == tc)
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
        /// Gets a formatted list of members with their signatures and types
        /// </summary>
        public static List<string> GetFormattedMembersList(ITypeSymbol objectType, string requestedName)
        {
            const double MinScore = 0.3;

            var result = new List<(string name, string displayName, double score)>();

            // Process all members of the object type
            foreach (var member in objectType.GetMembers())
            {
                try
                {
                    string displayName = member.Name;

                    // Check if it's a method or property
                    if (member is IMethodSymbol methodSymbol)
                    {
                        // For methods, add its signature
                        var parameters = methodSymbol.Parameters
                            .Select(p => $"{p.Name}: {p.Type}")
                            .ToList();

                        string paramString = string.Join(", ", parameters);
                        string returnType = methodSymbol.ReturnType.ToString();

                        displayName = $"{member.Name}({paramString})";
                        if (returnType != "void")
                        {
                            displayName += $": {returnType}";
                        }
                    }
                    else if (member is IPropertySymbol propertySymbol)
                    {
                        // For properties, add its type
                        displayName = $"{member.Name}: {propertySymbol.Type}";
                    }
                    else if (member is IFieldSymbol fieldSymbol)
                    {
                        // For fields, add its type
                        displayName = $"{member.Name}: {fieldSymbol.Type}";
                    }

                    // Calculate similarity with requested name
                    double score = ComputeCompositeScore(requestedName, member.Name);

                    result.Add((member.Name, displayName, score));
                }
                catch
                {
                    // In case of error, add just the member name
                    double score = ComputeCompositeScore(requestedName, member.Name);
                    result.Add((member.Name, member.Name, score));
                }
            }

            // Sort by similarity and take only top 5 items with scores above threshold
            return [.. result
                .Where(item => item.score >= MinScore)
                .OrderByDescending(item => item.score)
                .Take(5)
                .Select(item => item.displayName)];
        }

        /// <summary>
        /// Finds possible exported symbols that match the requested name
        /// </summary>
        public static List<(string name, double score)> FindPossibleExports(
            string requestedName,
            INamespaceSymbol moduleSymbol)
        {
            if (moduleSymbol == null)
            {
                return [];
            }

            try
            {
                var exports = moduleSymbol.GetMembers();
                const double MinScore = 0.3;

                // Calculate similarity scores, filter by threshold, sort by score and return top 5
                return [.. exports
                    .Select(exportSymbol => (exportSymbol.Name, ComputeCompositeScore(requestedName, exportSymbol.Name)))
                    .Where(item => item.Item2 > MinScore)
                    .OrderByDescending(item => item.Item2)
                    .Take(5)
                    .Select(item => (item.Name, item.Item2))];
            }
            catch
            {
                return [];
            }
        }

        /// <summary>
        /// Finds similar local symbols in the current scope
        /// </summary>
        public static List<(string name, double score)> FindSimilarLocalSymbols(
            SemanticModel semanticModel,
            SyntaxNode node,
            string name)
        {
            const double MinScore = 0.3;

            // Find symbols in the current scope
            var symbols = semanticModel.LookupSymbols(node.SpanStart);

            // Calculate similarity for each symbol, then remove duplicates, sort by score, and take top 5
            return [.. symbols
                .Select(symbol => (symbol.Name, ComputeCompositeScore(name, symbol.Name)))
                .Where(item => item.Item2 >= MinScore)
                .GroupBy(item => item.Name)
                .Select(group => group.First())
                .OrderByDescending(item => item.Item2)
                .Take(5)];
        }
    }
}