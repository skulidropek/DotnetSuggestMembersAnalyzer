using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SuggestMembersAnalyzer.Tests
{
    public class InternalsVisibleToTests
    {
        [Fact]
        public void AssemblyHasInternalsVisibleToAttribute()
        {
            // Arrange
            var assembly = typeof(SuggestMembersAnalyzer).Assembly;
            
            // Act
            var internalsVisibleToAttributes = assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>();
            
            // Assert
            Assert.Contains(internalsVisibleToAttributes, attr => attr.AssemblyName.Contains("SuggestMembersAnalyzer.Tests"));
        }
    }
} 