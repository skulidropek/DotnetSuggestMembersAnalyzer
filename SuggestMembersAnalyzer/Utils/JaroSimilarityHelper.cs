// <copyright file="JaroSimilarityHelper.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    using System;

    /// <summary>
    /// Helper methods for Jaro similarity calculation to reduce method complexity.
    /// </summary>
    internal static class JaroSimilarityHelper
    {
        /// <summary>
        /// Number of components in Jaro similarity calculation.
        /// </summary>
        private const double JaroComponents = 3.0;

        /// <summary>
        /// Divisor for transposition calculation.
        /// </summary>
        private const int TranspositionDivisor = 2;

        /// <summary>
        /// Calculates the final Jaro score from match and transposition data.
        /// </summary>
        /// <param name="matches">Number of matching characters.</param>
        /// <param name="transpositions">Number of transpositions.</param>
        /// <param name="len1">Length of first string.</param>
        /// <param name="len2">Length of second string.</param>
        /// <returns>Jaro similarity score.</returns>
        internal static double CalculateJaroScore(int matches, int transpositions, int len1, int len2)
        {
            return matches <= 0 || len1 <= 0 || len2 <= 0
                ? 0.0
                : (((double)matches / len1) +
                   ((double)matches / len2) +
                   (((double)matches - transpositions) / matches)) / JaroComponents;
        }

        /// <summary>
        /// Counts transpositions between matched characters.
        /// </summary>
        /// <param name="s1">First string.</param>
        /// <param name="s2">Second string.</param>
        /// <param name="s1Matches">Match indicators for first string.</param>
        /// <param name="s2Matches">Match indicators for second string.</param>
        /// <returns>Number of transpositions.</returns>
        internal static int CountTranspositions(string s1, string s2, bool[] s1Matches, bool[] s2Matches)
        {
            if (s1 is null || s2 is null || s1Matches is null || s2Matches is null)
            {
                return 0;
            }

            if (s1Matches.Length != s1.Length || s2Matches.Length != s2.Length)
            {
                return 0;
            }

            int transpositions = 0;
            int k = 0;

            for (int i = s1.Length - 1; i >= 0; i--)
            {
                if (!s1Matches[i])
                {
                    continue;
                }

                // Safe boundary check to prevent infinite loop
                while (k < s2Matches.Length && !s2Matches[k])
                {
                    k++;
                }

                // Additional safety check
                if (k >= s2Matches.Length)
                {
                    break;
                }

                if (s1[i] != s2[k])
                {
                    transpositions++;
                }

                k++;
            }

            return Math.DivRem(transpositions, TranspositionDivisor, out _);
        }

        /// <summary>
        /// Finds matching characters between two strings within the match distance.
        /// </summary>
        /// <param name="s1">First string.</param>
        /// <param name="s2">Second string.</param>
        /// <param name="matchDistance">Maximum distance for character matching.</param>
        /// <returns>Match data including counts and arrays.</returns>
        internal static (int matches, bool[] s1Matches, bool[] s2Matches) FindMatches(
            string s1, string s2, int matchDistance)
        {
            if (s1 is null || s2 is null)
            {
                return (0, [], []);
            }

            int len1 = s1.Length;
            int len2 = s2.Length;

            if (len1 == 0 || len2 == 0)
            {
                return (0, new bool[len1], new bool[len2]);
            }

            bool[] s1Matches = new bool[len1];
            bool[] s2Matches = new bool[len2];
            int matches = 0;

            for (int i = len1 - 1; i >= 0; i--)
            {
                int start = Math.Max(0, i - matchDistance);
                int end = Math.Min(i + matchDistance + 1, len2);

                for (int j = end - 1; j >= start; j--)
                {
                    if (s2Matches[j] || s1[i] != s2[j])
                    {
                        continue;
                    }

                    s1Matches[i] = true;
                    s2Matches[j] = true;
                    matches++;
                    break;
                }
            }

            return (matches, s1Matches, s2Matches);
        }
    }
}