using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace SuggestMembersAnalyzer.Tests
{
    public class SuggestMembersAnalyzerTests
    {
        [Fact]
        public void SuggestMembersAnalyzer_ObjectInitializer_DetectsMissingMember()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            var obj = new TestObject
            {
                MissingProperty = 42 // Должно вызвать диагностику
            };
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
            Assert.Contains("MissingProperty", diagnostics[0].GetMessage());
            Assert.Contains("ValidProperty", diagnostics[0].GetMessage());
        }

        [Fact]
        public void SuggestMembersAnalyzer_NestedObjectInitializer_DetectsMissingMember()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            var obj = new RootObject
            {
                ValidProperty = new ChildObject
                {
                    MissingNestedProperty = 42 // Должно вызвать диагностику
                }
            };
        }
    }

    class RootObject
    {
        public ChildObject ValidProperty { get; set; }
    }

    class ChildObject
    {
        public int ValidNestedProperty { get; set; }
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert
            Assert.Single(diagnostics);
            Assert.Equal("SMB001", diagnostics[0].Id);
            Assert.Contains("MissingNestedProperty", diagnostics[0].GetMessage());
            Assert.Contains("ValidNestedProperty", diagnostics[0].GetMessage());
        }

        [Fact]
        public void SuggestMembersAnalyzer_ConditionalAccessExpression_DetectsMissingMember()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            TestObject obj = null;
            var value = obj?.MissingProperty; // Должно вызвать диагностику
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
            Assert.Contains("MissingProperty", diagnostics[0].GetMessage());
            Assert.Contains("ValidProperty", diagnostics[0].GetMessage());
        }

        [Fact]
        public void SuggestMembersAnalyzer_MemberAccessInAttribute_DoesNotReport()
        {
            // Arrange
            string code = @"
using System;

namespace TestNamespace
{
    [AttributeUsage(AttributeTargets.All)]
    public class TestAttribute : Attribute
    {
        public TestAttribute(string name) { }
    }

    [Test(NonExistentProperty = 42)] // Не должно вызывать диагностику
    class TestClass
    {
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB001"));
        }

        [Fact]
        public void SuggestMembersAnalyzer_OverloadResolutionFailure_DoesNotReport()
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
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB001"));
        }

        [Fact]
        public void SuggestMembersAnalyzer_SymbolTypeResolution_HandlesComplexTypes()
        {
            // Arrange
            string code = @"
namespace TestNamespace
{
    class TestClass
    {
        void Method()
        {
            TestObject obj = GetObject();
            obj.MissingProperty = 42; // Должно вызвать диагностику
        }

        TestObject GetObject() => new TestObject();
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
            Assert.Contains("MissingProperty", diagnostics[0].GetMessage());
            Assert.Contains("ValidProperty", diagnostics[0].GetMessage());
        }

        [Fact]
        public void Test_NullTargetExpressionType_ShouldNotReportDiagnostic()
        {
            // Arrange
            var test = @"
using System;
using System.Dynamic;

class Program
{
    void Method()
    {
        var obj = GetObject();
        obj.NonExistentMember = 42; // This should not trigger the analyzer
    }

    dynamic GetObject() 
    {
        // Returns dynamic type that has a null compile-time type
        return new ExpandoObject();
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(test);

            // Assert - no diagnostics should be reported for dynamic objects
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB001"));
        }

        [Fact]
        public void Test_AttributeSyntax_ShouldNotReportDiagnostic()
        {
            // Arrange
            var test = @"
using System;

[NonExistentAttribute]
class Program
{
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(test);

            // Assert - no diagnostic should be reported for attributes
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB001"));
        }

        [Fact]
        public void Test_NestedInitializers_ReportsCorrectDiagnostic()
        {
            // Arrange
            var test = @"
using System;

class Address
{
    public string Street { get; set; }
}

class Person
{
    public Address HomeAddress { get; set; }
}

class Program
{
    void Method()
    {
        var person = new Person
        {
            HomeAddress = new Address
            {
                NonExistentProperty = ""123 Main St"" // This should be reported
            }
        };
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(test);

            // Assert
            var diagnostic = diagnostics.FirstOrDefault(d => d.Id == "SMB001");
            Assert.NotNull(diagnostic);
            Assert.Contains("NonExistentProperty", diagnostic.GetMessage());
            Assert.Contains("Address", diagnostic.GetMessage());
        }

        [Fact]
        public void Test_ConditionalAccess_ReportsCorrectDiagnostic()
        {
            // Arrange
            var test = @"
using System;

class Person
{
    public string Name { get; set; }
}

class Program
{
    void Method()
    {
        Person person = null;
        var result = person?.NonExistentProperty; // This should be reported
    }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(test);

            // Assert
            var diagnostic = diagnostics.FirstOrDefault(d => d.Id == "SMB001");
            Assert.NotNull(diagnostic);
            Assert.Contains("NonExistentProperty", diagnostic.GetMessage());
            Assert.Contains("Person", diagnostic.GetMessage());
        }

        [Fact]
        public void SuggestMembersAnalyzer_AttributeExpression_DoesNotReport()
        {
            // Arrange
            string code = @"
using System;

[MissingAttribute]
class TestClass
{
}

class CustomAttribute : Attribute
{
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert - No diagnostic should be reported for attribute expressions
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB001"));
        }

        [Fact]
        public void SuggestMembersAnalyzer_MemberAttributeExpression_DoesNotReport()
        {
            // Arrange
            string code = @"
using System;

class TestClass
{
    [MissingAttribute]
    public void Method() {}
}

class CustomAttribute : Attribute
{
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert - No diagnostic should be reported for attribute expressions
            Assert.Empty(diagnostics.Where(d => d.Id == "SMB001"));
        }

        [Fact]
        public void SuggestMembersAnalyzer_ComplexTargetExpression_ExtractsSymbolType()
        {
            // Arrange
            string code = @"
class TestClass
{
    void Method()
    {
        var obj = GetObject();
        obj.Field.MissingProperty = 42; // This triggers diagnostics with Field as target
    }

    TestObject GetObject() => new TestObject();
}

class TestObject
{
    public SubObject Field { get; set; } = new SubObject();
}

class SubObject
{
    public int ValidProperty { get; set; }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert
            var diagnostic = diagnostics.FirstOrDefault(d => d.Id == "SMB001");
            Assert.NotNull(diagnostic);
            Assert.Contains("MissingProperty", diagnostic.GetMessage());
            Assert.Contains("SubObject", diagnostic.GetMessage());
            Assert.Contains("ValidProperty", diagnostic.GetMessage());
        }

        [Fact]
        public void SuggestMembersAnalyzer_ParameterTargetExpression_ExtractsSymbolType()
        {
            // Arrange
            string code = @"
class TestClass
{
    void Method(TestObject param)
    {
        param.MissingProperty = 42; // This triggers diagnostics with param as target
    }
}

class TestObject
{
    public int ValidProperty { get; set; }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert
            var diagnostic = diagnostics.FirstOrDefault(d => d.Id == "SMB001");
            Assert.NotNull(diagnostic);
            Assert.Contains("MissingProperty", diagnostic.GetMessage());
            Assert.Contains("TestObject", diagnostic.GetMessage());
            Assert.Contains("ValidProperty", diagnostic.GetMessage());
        }

        [Fact]
        public void SuggestMembersAnalyzer_MethodReturnTargetExpression_ExtractsSymbolType()
        {
            // Arrange
            string code = @"
class TestClass
{
    void Method()
    {
        GetObject().MissingProperty = 42; // This triggers diagnostics using method return type
    }

    TestObject GetObject() => new TestObject();
}

class TestObject
{
    public int ValidProperty { get; set; }
}";

            // Act
            var diagnostics = GetDiagnostics<SuggestMembersAnalyzer>(code);

            // Assert
            var diagnostic = diagnostics.FirstOrDefault(d => d.Id == "SMB001");
            Assert.NotNull(diagnostic);
            Assert.Contains("MissingProperty", diagnostic.GetMessage());
            Assert.Contains("TestObject", diagnostic.GetMessage());
            Assert.Contains("ValidProperty", diagnostic.GetMessage());
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
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
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