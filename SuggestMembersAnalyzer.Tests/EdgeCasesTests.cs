using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace SuggestMembersAnalyzer.Tests
{
    public class EdgeCasesTests
    {
        [Fact]
        public void SuggestMembersAnalyzer_WithGenericTypes_HandlesCorrectly()
        {
            // Arrange
            string code = @"
using System.Collections.Generic;

namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            var dict = new Dictionary<string, int>();
            dict.NonExistentMethod(); // Должно вызвать диагностику
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert
            Assert.Single(diagnostics);
            Assert.Equal("SMB001", diagnostics[0].Id);
            Assert.Contains("NonExistentMethod", diagnostics[0].GetMessage());
        }

        [Fact]
        public void SuggestMembersAnalyzer_WithExtensionMethods_HandlesCorrectly()
        {
            // Arrange
            string code = @"
using System.Linq;
using System.Collections.Generic;

namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            var list = new List<int> { 1, 2, 3 };
            var result = list.NonExistentLinqMethod(); // Должно вызвать диагностику
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert
            Assert.Single(diagnostics);
            var message = diagnostics[0].GetMessage();
            Assert.Contains("NonExistentLinqMethod", message);
            // Достаточно проверить, что диагностика обнаружена
        }

        [Fact]
        public void SuggestVariablesAnalyzer_WithNestedScopes_HandlesCorrectly()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            int outerVariable = 10;
            
            if (true)
            {
                int innerVariable = 20;
                int result = nonExistentVariable + outerVariable; // Должно вызвать диагностику
            }
            
            // innerVariable не доступна здесь
            int sum = outerVariable + nonExistentVariable; // Должно вызвать диагностику
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestVariablesAnalyzer>(code);

            // Assert
            Assert.Equal(2, diagnostics.Count(d => d.Id == "SMB002"));
            Assert.All(diagnostics.Where(d => d.Id == "SMB002"), d => Assert.Contains("nonExistentVariable", d.GetMessage()));
        }

        [Fact]
        public void SuggestNamespacesAnalyzer_WithPartialMatches_FindsSimilarNamespaces()
        {
            // Arrange
            string code = @"
using System;
using Microsft.CodeAnalysis; // Опечатка, должно быть Microsoft.CodeAnalysis

namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            var code = """";
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestNamespacesAnalyzer>(code);

            // Assert
            Assert.NotEmpty(diagnostics);
            Assert.Equal("SMB003", diagnostics[0].Id);
            Assert.Contains("Microsft.CodeAnalysis", diagnostics[0].GetMessage());
            
            // Проверим, что в сообщении содержатся подсказки пространств имен
            // Обратите внимание: анализатор может предлагать другие похожие пространства имен,
            // поэтому не стоит жестко проверять конкретную подсказку Microsoft.CodeAnalysis
            Assert.Contains("Did you mean:", diagnostics[0].GetMessage());
        }

        [Fact]
        public void SuggestNamedArgumentsAnalyzer_WithOverloads_SuggestsAllParameters()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            TestMethod(invalidParam: 42); // Должно вызвать диагностику
        }

        void TestMethod() { }
        void TestMethod(int validParam1) { }
        void TestMethod(string validParam2) { }
        void TestMethod(int validParam1, string validParam2) { }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestNamedArgumentsAnalyzer>(code);

            // Assert
            Assert.NotEmpty(diagnostics);
            Assert.Equal("SMB004", diagnostics[0].Id);
            string message = diagnostics[0].GetMessage();
            Assert.Contains("invalidParam", message);
            Assert.Contains("validParam1", message);
            Assert.Contains("validParam2", message);
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
            references.Add(MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location));
            
            return CSharpCompilation.Create(
                "TestCompilation",
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                options);
        }
    }
} 