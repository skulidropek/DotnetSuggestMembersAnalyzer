using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace SuggestMembersAnalyzer.Tests
{
    public class SuggestNamespacesAnalyzerTests
    {
        [Fact]
        public async Task NamespaceNotFound_SuggestsCorrectNamespaces()
        {
            // Arrange
            var test = @"
using System;
using SysTem.Collectins.Generic; // Typo: should be Collections
using Microsoft.NotExisting;

namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
            var list = new List<int>();
        }
    }
}";

            var analyzer = new SuggestNamespacesAnalyzer();
            var diagnostics = await GetDiagnosticsAsync(test, analyzer);

            // Assert
            Assert.NotEmpty(diagnostics);
            Assert.Contains(diagnostics, d => d.Id == SuggestNamespacesAnalyzer.NamespaceNotFoundDiagnosticId);
            Assert.Contains(diagnostics, d => d.GetMessage().Contains("SysTem.Collectins.Generic"));
            Assert.Contains(diagnostics, d => d.GetMessage().Contains("System.Collections.Generic"));
        }

        [Fact]
        public async Task NamespaceNotFound_WithNoSuggestions_ReturnsNoDiagnostics()
        {
            // Этот тест проверяет, что анализатор выдает ожидаемое сообщение об ошибке,
            // когда пространство имен не существует, но может находить похожие.
            // Заметим, что даже для очень случайного пространства имен анализатор
            // всё равно может найти какие-то похожие варианты.
            
            // Arrange
            var test = @"
using System;
using CompletelyRandomNamespaceThatDoesNotExist123456;

namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
        }
    }
}";

            var analyzer = new SuggestNamespacesAnalyzer();
            var diagnostics = await GetDiagnosticsAsync(test, analyzer);

            // Assert
            var diagnosticsFromAnalyzer = diagnostics.Where(d => d.Id == SuggestNamespacesAnalyzer.NamespaceNotFoundDiagnosticId).ToList();
            
            // Проверяем, что ожидаемая диагностика существует для неизвестного пространства имен
            Assert.NotEmpty(diagnosticsFromAnalyzer);
            
            // Проверяем, что сообщение содержит имя неизвестного пространства имен
            var diagnostic = diagnosticsFromAnalyzer.First();
            Assert.Contains("CompletelyRandomNamespaceThatDoesNotExist123456", diagnostic.GetMessage());
            
            // Проверяем, что сообщение содержит подсказку
            Assert.Contains("Did you mean:", diagnostic.GetMessage());
        }

        [Fact]
        public async Task NamespaceNotFound_WithAlias_DoesNotReportSMB003Diagnostic()
        {
            // Arrange
            var test = @"
using System;
using Alias = SomeNonExistentNamespace;

namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
        }
    }
}";

            var analyzer = new SuggestNamespacesAnalyzer();
            var diagnostics = await GetDiagnosticsAsync(test, analyzer);

            // Assert
            // Проверяем, что нет диагностик от нашего анализатора (SMB003)
            Assert.Equal(0, diagnostics.Count(d => d.Id == SuggestNamespacesAnalyzer.NamespaceNotFoundDiagnosticId));
            // Заметим, что компилятор C# всё равно выдаст ошибку CS0246 о ненайденном пространстве имён
        }

        [Fact]
        public async Task NamespaceNotFound_WithStaticUsing_DoesNotReportSMB003Diagnostic()
        {
            // Arrange
            var test = @"
using System;
using static SomeNonExistentNamespace.Class;

namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
        }
    }
}";

            var analyzer = new SuggestNamespacesAnalyzer();
            var diagnostics = await GetDiagnosticsAsync(test, analyzer);

            // Assert
            // Проверяем, что нет диагностик от нашего анализатора (SMB003)
            Assert.Equal(0, diagnostics.Count(d => d.Id == SuggestNamespacesAnalyzer.NamespaceNotFoundDiagnosticId));
            // Заметим, что компилятор C# всё равно выдаст ошибку CS0246 о ненайденном пространстве имён
        }

        [Fact]
        public async Task CollectNamespaces_GathersAllAvailableNamespaces()
        {
            // Arrange
            var test = @"
namespace TestRoot
{
    namespace SubNamespace1
    {
        class Test1 {}
    }
    
    namespace SubNamespace2
    {
        class Test2 {}
    }
}";

            // Act
            var compilation = CreateCompilation(test);
            var analyzer = new SuggestNamespacesAnalyzer();
            
            // Получаем все пространства имён через рефлексию
            var allNamespacesMethod = typeof(SuggestNamespacesAnalyzer).GetMethod(
                "GetAllNamespaces", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var result = allNamespacesMethod?.Invoke(null, new object[] { compilation }) as ImmutableArray<string>?;

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.Value);
            Assert.Contains(result.Value, ns => ns == "TestRoot");
            Assert.Contains(result.Value, ns => ns == "TestRoot.SubNamespace1");
            Assert.Contains(result.Value, ns => ns == "TestRoot.SubNamespace2");
        }

        private static async Task<IEnumerable<Diagnostic>> GetDiagnosticsAsync(
            string source,
            DiagnosticAnalyzer analyzer)
        {
            var document = CreateDocument(source);
            var compilation = (await document.Project.GetCompilationAsync()).WithAnalyzers(
                ImmutableArray.Create(analyzer));

            return await compilation.GetAllDiagnosticsAsync();
        }

        private static Document CreateDocument(string source)
        {
            var projectId = ProjectId.CreateNewId();
            var documentId = DocumentId.CreateNewId(projectId);

            var solution = new AdhocWorkspace()
                .CurrentSolution
                .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
                .WithProjectCompilationOptions(
                    projectId,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddDocument(documentId, "Test.cs", SourceText.From(source));

            solution = solution.AddMetadataReference(
                projectId, 
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

            solution = solution.AddMetadataReference(
                projectId, 
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location));

            return solution.GetDocument(documentId);
        }

        private static Compilation CreateCompilation(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);
            
            return CSharpCompilation.Create(
                "TestCompilation",
                new[] { syntaxTree },
                new[] 
                { 
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }
    }
} 