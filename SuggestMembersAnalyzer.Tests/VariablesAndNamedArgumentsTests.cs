using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace SuggestMembersAnalyzer.Tests
{
    public class VariablesAndNamedArgumentsTests
    {
        [Fact]
        public void SuggestVariablesAnalyzer_KeywordAsIdentifier_DoesNotReport()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            int @int = 42; // Использование ключевого слова как идентификатора
            int int = 10; // var - это ключевое слово, не должно вызывать диагностику
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestVariablesAnalyzer>(code);

            // Assert
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB002"));
        }

        [Fact]
        public void SuggestVariablesAnalyzer_VariableInUsingDirective_DoesNotReport()
        {
            // Arrange
            string code = @"
using System;
using nonExistingNamespace; // Этим должен заниматься SuggestNamespacesAnalyzer

namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            int value = 42;
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestVariablesAnalyzer>(code);

            // Assert
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB002"));
        }

        [Fact]
        public void SuggestVariablesAnalyzer_NameOfExpression_DoesNotReport()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            string propertyName = nameof(nonExistentProperty); // В nameof не должно быть диагностики
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestVariablesAnalyzer>(code);

            // Assert
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB002"));
        }

        [Fact]
        public void SuggestVariablesAnalyzer_LinqExpressions_DoesNotReport()
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
            var query = from item in list
                        let nonExistentVariable = item * 2 // В LINQ не должно быть диагностики
                        select new { item, doubled = nonExistentVariable };
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestVariablesAnalyzer>(code);

            // Assert
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB002"));
        }

        [Fact]
        public void SuggestVariablesAnalyzer_OverloadResolutionFailure_DoesNotReport()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            var obj = new TestObject();
            obj.Overloaded(""string""); // Должно быть разрешено как перегрузка
        }
    }

    class TestObject
    {
        public void Overloaded(int num) { }
        public void Overloaded(double num) { }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestVariablesAnalyzer>(code);

            // Assert
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB002"));
        }

        [Fact]
        public void SuggestNamedArgumentsAnalyzer_WithAlias_DoesNotReport()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            TestMethod(param: 42); // Корректно
        }

        void TestMethod(int param = 0) { }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestNamedArgumentsAnalyzer>(code);

            // Assert
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void SuggestNamedArgumentsAnalyzer_MultipleOverloads_ReportsAllSignatures()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            TestMethod(wrongName: 42); // Неверное имя параметра
        }

        void TestMethod(int param1) { }
        void TestMethod(string param2) { }
        void TestMethod(int param1, string param2) { }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestNamedArgumentsAnalyzer>(code);

            // Assert
            Assert.Single(diagnostics);
            Assert.Equal("SMB004", diagnostics[0].Id);
            Assert.Contains("wrongName", diagnostics[0].GetMessage());
            Assert.Contains("TestMethod(int param1)", diagnostics[0].GetMessage());
            Assert.Contains("TestMethod(string param2)", diagnostics[0].GetMessage());
            Assert.Contains("TestMethod(int param1, string param2)", diagnostics[0].GetMessage());
        }

        [Fact]
        public void SuggestNamedArgumentsAnalyzer_Constructor_ReportsForMissingParameter()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            var obj = new TestObject(wrongParam: 42); // Неверное имя параметра
        }
    }

    class TestObject
    {
        public TestObject(int correctParam) { }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestNamedArgumentsAnalyzer>(code);

            // Assert
            Assert.Single(diagnostics);
            Assert.Equal("SMB004", diagnostics[0].Id);
            Assert.Contains("wrongParam", diagnostics[0].GetMessage());
            Assert.Contains("TestObject(int correctParam)", diagnostics[0].GetMessage());
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