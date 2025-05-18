using System;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;

namespace StringSplitterTest
{
    class Program
    {
        static void Main(string[] args)
        {
            // Тестируем конкретные строки из нашего теста
            string longStr = "abcdefghijklmnopqrst";
            string shortStr = "abc";
            
            Console.WriteLine("== Сравнение длинной и короткой строки ==");
            double scoreLongToShort = ComputeCompositeScore(longStr, shortStr);
            double scoreShortToLong = ComputeCompositeScore(shortStr, longStr);
            
            Console.WriteLine($"Long to short score: {scoreLongToShort}");
            Console.WriteLine($"Short to long score: {scoreShortToLong}");
            Console.WriteLine($"Long to short > Short to long: {scoreLongToShort > scoreShortToLong}");
            Console.WriteLine();
        }
        
        // Копия метода из StringSimilarity.cs
        static double ComputeCompositeScore(string unknown, string candidate)
        {
            // Проверка на null, чтобы избежать NullReferenceException
            unknown = unknown ?? string.Empty;
            candidate = candidate ?? string.Empty;
            
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
            
            double finalScore = baseSimilarity + exactBonus + containmentBonus + tokenBonus - lengthPenalty;
            
            Console.WriteLine($"  Query: \"{unknown}\", Candidate: \"{candidate}\"");
            Console.WriteLine($"  Base similarity: {baseSimilarity}");
            Console.WriteLine($"  Exact bonus: {exactBonus}");
            Console.WriteLine($"  Containment bonus: {containmentBonus}");
            Console.WriteLine($"  Token bonus: {tokenBonus}");
            Console.WriteLine($"  Length penalty: {lengthPenalty}");
            
            return finalScore;
        }
        
        // Копия метода из StringSimilarity.cs
        static string[] SplitIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return Array.Empty<string>();
            }
            
            return [.. Regex.Split(identifier, @"(?=[A-Z])|[_\s\d]")
                .Select(static s => s.ToLowerInvariant())
                .Where(static s => !string.IsNullOrEmpty(s))];
        }
        
        // Копия метода из StringSimilarity.cs
        static string Normalize(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return string.Empty;
            }
            
            return Regex.Replace(str.ToLowerInvariant(), @"[_\s]", "");
        }
        
        // Копия метода из StringSimilarity.cs
        static double JaroWinkler(string s1, string s2)
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
        
        // Копия метода из StringSimilarity.cs
        static double Jaro(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
            {
                return 1.0;
            }

            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            {
                return 0.0;
            }

            if (s1 == s2)
            {
                return 1.0;
            }

            // Calculate matching window size
            int windowSize = Math.Max(0, Math.Max(s1.Length, s2.Length) / 2 - 1);

            // Create flags for matched characters
            bool[] s1Matches = new bool[s1.Length];
            bool[] s2Matches = new bool[s2.Length];

            // Count matching characters within the window
            int matchingChars = 0;
            for (int i = 0; i < s1.Length; i++)
            {
                // Calculate window boundaries for the current character
                int start = Math.Max(0, i - windowSize);
                int end = Math.Min(i + windowSize + 1, s2.Length);

                for (int j = start; j < end; j++)
                {
                    // Skip if already matched or not the same character
                    if (s2Matches[j] || s1[i] != s2[j])
                    {
                        continue;
                    }

                    // Mark as matched and increment count
                    s1Matches[i] = true;
                    s2Matches[j] = true;
                    matchingChars++;
                    break;
                }
            }

            // If no characters match, return 0
            if (matchingChars == 0)
            {
                return 0.0;
            }

            // Count transpositions
            int transpositions = 0;
            int j2 = 0;

            for (int i = 0; i < s1.Length; i++)
            {
                if (!s1Matches[i])
                {
                    continue;
                }

                // Find the next matched character in s2
                while (!s2Matches[j2])
                {
                    j2++;
                }

                // If the characters don't match, it's a transposition
                if (s1[i] != s2[j2])
                {
                    transpositions++;
                }

                j2++;
            }

            // Calculate Jaro similarity
            double m = matchingChars;
            return (m / s1.Length + m / s2.Length + (m - transpositions / 2.0) / m) / 3.0;
        }
    }
}
