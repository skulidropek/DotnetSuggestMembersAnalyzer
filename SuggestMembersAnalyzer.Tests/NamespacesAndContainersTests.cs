using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace SuggestMembersAnalyzer.Tests
{
    public class NamespacesAndContainersTests
    {
        [Fact]
        public void SuggestNamespacesAnalyzer_WithSimilarNamespaces_SuggestsCorrectOptions()
        {
            // Arrange
            string code = @"
using System;
using Systm.Linq; // Опечатка, должно быть System.Linq
using System.Collections.Generic;

namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            var list = new List<int>();
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestNamespacesAnalyzer>(code);

            // Assert
            Assert.NotEmpty(diagnostics);
            Assert.Equal("SMB003", diagnostics[0].Id);
            Assert.Contains("Systm.Linq", diagnostics[0].GetMessage());
            Assert.Contains("System.Linq", diagnostics[0].GetMessage());
        }

        [Fact]
        public void SuggestNamespacesAnalyzer_WithMultipleOptions_SuggestsAllOptions()
        {
            // Arrange
            string code = @"
using System;
using Systm.IO; // Опечатка, должно быть System.IO

namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            var file = new FileInfo(""test.txt"");
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestNamespacesAnalyzer>(code);

            // Assert
            Assert.NotEmpty(diagnostics);
            Assert.Equal("SMB003", diagnostics[0].Id);
            Assert.Contains("Systm.IO", diagnostics[0].GetMessage());
            // В сообщении должны быть варианты исправления
            Assert.Contains("System.IO", diagnostics[0].GetMessage());
        }

        [Fact]
        public void SuggestNamespacesAnalyzer_WithAliasDirective_DoesNotReport()
        {
            // Arrange
            string code = @"
using System;
using IO = System.IO;

namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            var file = IO.File.ReadAllText(""test.txt"");
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestNamespacesAnalyzer>(code);

            // Assert
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB003"));
        }

        [Fact]
        public void SuggestNamespacesAnalyzer_WithStaticUsing_DoesNotReport()
        {
            // Arrange
            string code = @"
using System;
using static System.Console;

namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            WriteLine(""Hello""); // Использование через статический импорт
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestNamespacesAnalyzer>(code);

            // Assert
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB003"));
        }

        private ImmutableArray<Diagnostic> GetDiagnostics<TAnalyzer>(string code)
            where TAnalyzer : DiagnosticAnalyzer, new()
        {
            var compilation = CreateCompilation(code);
            var analyzer = new TAnalyzer();
            var compilationWithAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));
            return compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().Result;
        }

        private static Compilation CreateCompilation(string source)
        {
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToList();

            // Добавляем базовые ссылки .NET
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(Console).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(System.IO.FileInfo).Assembly.Location));

            return CSharpCompilation.Create(
                "TestCompilation",
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                options);
        }
    }
} 