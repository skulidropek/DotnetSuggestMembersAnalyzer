using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SuggestMembersAnalyzer.Utils
{
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
                .Select(static s => s.ToLowerInvariant())
                .Where(static s => !string.IsNullOrEmpty(s))];
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
                            .Select(static p => $"{p.Name}: {p.Type}")
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
                .Where(static item => item.score >= MinScore)
                .OrderByDescending(static item => item.score)
                .Take(5)
                .Select(static item => item.displayName)];
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
        /// Finds similar symbols from a list of key-value tuples where keys are identifiers for search
        /// and values are the full representations to insert. Allows duplicate keys.
        /// </summary>
        /// <typeparam name="TValue">Type of value to return in results</typeparam>
        /// <param name="queryName">The name being searched for</param>
        /// <param name="candidateEntries">List of candidate entries with search key and display value</param>
        /// <returns>List of matching items with scores</returns>
        public static List<(string Name, TValue Value, double Score)> FindSimilarSymbols<TValue>(
            string queryName,
            IEnumerable<(string Key, TValue Value)> candidateEntries)
        {
            const double MIN_SCORE = 0.3;
            
            // Process the provided candidate entries
            return [.. candidateEntries
                .Select(entry => (
                    Name: entry.Key, 
                    Value: entry.Value,
                    Score: ComputeCompositeScore(queryName, entry.Key)))
                .Where(item => !string.IsNullOrEmpty(item.Name) && 
                       item.Score >= MIN_SCORE && 
                       item.Name != queryName) // Exclude exact matches to the query, as these likely don't exist
                .OrderByDescending(r => r.Score)
                .Take(5)];
        }
     
        /// <summary>
        /// Finds similar names from a custom list of candidates (simple string array)
        /// </summary>
        public static List<(string Name, double Score)> FindSimilarLocalSymbols(
            string queryName,
            string[] candidateNames)
        {
            const double MIN_SCORE = 0.3;
            
            // Process the provided candidate names
            return [.. candidateNames
                .Where(candidate => !string.IsNullOrEmpty(candidate))
                .Select(candidate => (Name: candidate, Score: ComputeCompositeScore(queryName, candidate)))
                .Where(item => item.Score >= MIN_SCORE)
                .OrderByDescending(r => r.Score)
                .Take(5)];
        }
    }
}