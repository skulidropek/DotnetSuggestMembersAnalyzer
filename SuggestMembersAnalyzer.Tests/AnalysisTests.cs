using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace SuggestMembersAnalyzer.Tests;

public class AnalysisTests
{
    [Fact]
    public void SuggestMembersAnalyzer_DetectsMissingMember()
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
            obj.InvalidProperty = 42; // Должно вызвать диагностику
        }
    }

    class TestObject
    {
        public int ValidProperty { get; set; }
    }
}";

        // Act
        var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

        // Assert
        Assert.Single(diagnostics);
        Assert.Equal("SMB001", diagnostics[0].Id);
        Assert.Contains("InvalidProperty", diagnostics[0].GetMessage());
        Assert.Contains("ValidProperty", diagnostics[0].GetMessage());
    }

    [Fact]
    public void SuggestVariablesAnalyzer_DetectsMissingVariable()
    {
        // Arrange
        string code = @"
using System;

namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            int existingVariable = 10;
            Console.WriteLine(nonExistingVariable); // Должно вызвать диагностику
        }
    }
}";

        // Act
        var diagnostics = GetDiagnostics<SuggestVariablesAnalyzer>(code);

        // Assert
        // Может быть несколько диагностик, найдем ту, которая относится к nonExistingVariable
        var relevantDiagnostic = diagnostics.FirstOrDefault(d => d.GetMessage().Contains("nonExistingVariable"));
        Assert.NotNull(relevantDiagnostic);
        Assert.Equal("SMB002", relevantDiagnostic.Id);
        Assert.Contains("nonExistingVariable", relevantDiagnostic.GetMessage());
        Assert.Contains("existingVariable", relevantDiagnostic.GetMessage());
    }

    [Fact]
    public void SuggestNamespacesAnalyzer_DetectsMissingNamespace()
    {
        // Arrange
        string code = @"
using Systm; // Опечатка в System

namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            Console.WriteLine(""Hello"");
        }
    }
}";

        // Act
        var diagnostics = GetDiagnostics<SuggestNamespacesAnalyzer>(code);

        // Assert
        Assert.NotEmpty(diagnostics);
        Assert.Equal("SMB003", diagnostics[0].Id);
        Assert.Contains("Systm", diagnostics[0].GetMessage());
        Assert.Contains("System", diagnostics[0].GetMessage());
    }

    [Fact]
    public void SuggestNamedArgumentsAnalyzer_DetectsMissingNamedArgument()
    {
        // Arrange
        string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            TestMethod(invalidName: 42); // Должно вызвать диагностику
        }

        void TestMethod(int validName)
        {
        }
    }
}";

        // Act
        var diagnostics = GetDiagnostics<SuggestNamedArgumentsAnalyzer>(code);

        // Assert
        Assert.Single(diagnostics);
        Assert.Equal("SMB004", diagnostics[0].Id);
        Assert.Contains("invalidName", diagnostics[0].GetMessage());
        Assert.Contains("validName", diagnostics[0].GetMessage());
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