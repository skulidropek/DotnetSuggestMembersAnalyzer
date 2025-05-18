using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace SuggestMembersAnalyzer.Tests
{
    public class DiagnosticMessagesTests
    {
        [Fact]
        public void MemberNotFound_DiagnosticMessage_ContainsMemberAndType()
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
            obj.NonExistentMember = 42; // Должно вызвать диагностику
        }
    }

    class TestObject
    {
        public int ExistingMember { get; set; }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert
            Assert.Single(diagnostics);
            var diagnostic = diagnostics[0];
            Assert.Equal("SMB001", diagnostic.Id);
            string message = diagnostic.GetMessage();
            Assert.Contains("NonExistentMember", message);
            Assert.Contains("TestObject", message);
            Assert.Contains("ExistingMember", message);
        }

        [Fact]
        public void VariableNotFound_DiagnosticMessage_ContainsVariable()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            int existingVariable = 42;
            int result = nonExistentVariable; // Должно вызвать диагностику
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestVariablesAnalyzer>(code);

            // Assert
            var diagnostic = diagnostics.FirstOrDefault(d => d.Id == "SMB002");
            Assert.NotNull(diagnostic);
            string message = diagnostic.GetMessage();
            Assert.Contains("nonExistentVariable", message);
            // Проверяем наличие переменной в сообщении
        }

        [Fact]
        public void NamespaceNotFound_DiagnosticMessage_ContainsNamespaceAndSuggestions()
        {
            // Arrange
            string code = @"
using System;
using Systm.Collections.Generic; // Опечатка, должно быть System.Collections.Generic

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
            var diagnostic = diagnostics.FirstOrDefault(d => d.Id == "SMB003");
            Assert.NotNull(diagnostic);
            string message = diagnostic.GetMessage();
            Assert.Contains("Systm.Collections.Generic", message);
            Assert.Contains("System.Collections.Generic", message);
        }

        [Fact]
        public void NamedArgumentNotFound_DiagnosticMessage_ContainsParameterAndSuggestions()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            TestMethod(wrongParam: 42); // Должно вызвать диагностику
        }

        void TestMethod(int correctParam)
        {
        }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestNamedArgumentsAnalyzer>(code);

            // Assert
            Assert.NotEmpty(diagnostics);
            var diagnostic = diagnostics.FirstOrDefault(d => d.Id == "SMB004");
            Assert.NotNull(diagnostic);
            string message = diagnostic.GetMessage();
            Assert.Contains("wrongParam", message);
            Assert.Contains("correctParam", message);
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
            
            return CSharpCompilation.Create(
                "TestCompilation",
                new[] { CSharpSyntaxTree.ParseText(source) },
                references,
                options);
        }
    }
} 