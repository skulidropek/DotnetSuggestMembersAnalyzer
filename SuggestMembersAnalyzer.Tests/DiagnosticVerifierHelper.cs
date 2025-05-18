using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace SuggestMembersAnalyzer.Tests
{
    public class DiagnosticResult
    {
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
        public DiagnosticSeverity Severity { get; set; }
        public DiagnosticResultLocation[] Locations { get; set; }
        public IReadOnlyDictionary<string, string> Properties { get; set; }

        public DiagnosticResult()
        {
            Locations = Array.Empty<DiagnosticResultLocation>();
            Properties = new Dictionary<string, string>();
        }
    }

    public class DiagnosticResultLocation
    {
        public string Path { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }

        public DiagnosticResultLocation(string path, int line, int column)
        {
            Path = path ?? "";
            Line = line;
            Column = column;
        }
    }

    public static class DiagnosticVerifierHelper
    {
        public static void VerifyAnalyzerWithoutDiagnostics<TAnalyzer>(string source)
            where TAnalyzer : DiagnosticAnalyzer, new()
        {
            var diagnostics = GetDiagnostics<TAnalyzer>(source);
            Assert.Empty(diagnostics);
        }

        public static void VerifyAnalyzer<TAnalyzer>(string source, params DiagnosticResult[] expected)
            where TAnalyzer : DiagnosticAnalyzer, new()
        {
            var diagnostics = GetDiagnostics<TAnalyzer>(source).ToList();
            
            // Filter diagnostics by expected ones
            var filteredDiagnostics = diagnostics.Where(d => expected.Any(e => e.Id == d.Id)).ToList();
            
            Assert.Equal(expected.Length, filteredDiagnostics.Count);
            
            for (int i = 0; i < expected.Length; i++)
            {
                var expectedDiagnostic = expected[i];
                var actualDiagnostic = filteredDiagnostics.FirstOrDefault(d => d.Id == expectedDiagnostic.Id);
                
                Assert.NotNull(actualDiagnostic);
                Assert.StartsWith(expectedDiagnostic.Message, actualDiagnostic.GetMessage());
                Assert.Equal(expectedDiagnostic.Severity, actualDiagnostic.Severity);
            }
        }

        private static ImmutableArray<Diagnostic> GetDiagnostics<TAnalyzer>(string source)
            where TAnalyzer : DiagnosticAnalyzer, new()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Concat(new[] 
                { 
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
                });

            var compilation = CSharpCompilation.Create(
                "TestCompilation",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var analyzer = new TAnalyzer();
            // Create an array with the specific diagnostic analyzer type
            var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(analyzer);
            var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers);
            return compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().Result;
        }
    }
} 