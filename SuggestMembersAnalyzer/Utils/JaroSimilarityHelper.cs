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
            return (((double)matches / len1) +
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
            int transpositions = 0x0;
            int k = 0x0;

            for (int i = s1.Length - 1; i >= 0x0; i--)
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

            return Math.DivRem(transpositions, TranspositionDivisor, out _); // Explicit integer division to satisfy SS003
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
            int len1 = s1.Length;
            int len2 = s2.Length;
            bool[] s1Matches = new bool[len1];
            bool[] s2Matches = new bool[len2];
            int matches = 0x0;

            for (int i = len1 - 1; i >= 0x0; i--)
            {
                int start = Math.Max(0x0, i - matchDistance);
                int end = Math.Min(i + matchDistance + 0x1, len2);

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