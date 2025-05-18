using System;
using System.Reflection;
using Xunit;
using SuggestMembersAnalyzer;
using System.Linq;

namespace SuggestMembersAnalyzer.Tests
{
    public class ResourcesTests
    {
        [Fact]
        public void ResourceManager_NotNull()
        {
            // Assert
            Assert.NotNull(Resources.ResourceManager);
        }

        [Fact]
        public void AllProperties_ReturnValues()
        {
            // Act & Assert
            Assert.NotEmpty(Resources.MemberNotFoundTitle);
            Assert.NotEmpty(Resources.MemberNotFoundMessageFormat);
            Assert.NotEmpty(Resources.MemberNotFoundDescription);
            
            Assert.NotEmpty(Resources.VariableNotFoundTitle);
            Assert.NotEmpty(Resources.VariableNotFoundMessageFormat);
            Assert.NotEmpty(Resources.VariableNotFoundDescription);
            
            Assert.NotEmpty(Resources.NamespaceNotFoundTitle);
            Assert.NotEmpty(Resources.NamespaceNotFoundMessageFormat);
            Assert.NotEmpty(Resources.NamespaceNotFoundDescription);
            
            Assert.NotEmpty(Resources.NamedArgumentNotFoundTitle);
            Assert.NotEmpty(Resources.NamedArgumentNotFoundMessageFormat);
            Assert.NotEmpty(Resources.NamedArgumentNotFoundDescription);
        }

        [Fact]
        public void GetString_NonExistentKey_ReturnsDefaultValue()
        {
            // Arrange
            var getStringMethod = typeof(Resources).GetMethod("GetString", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            // Act
            var result = getStringMethod?.Invoke(null, new[] { "NonExistentKey" }) as string;
            
            // Assert
            Assert.NotNull(result);
            Assert.Equal("NonExistentKey", result); // Default behavior returns the key itself
        }

        [Fact]
        public void GetDefaultResourceString_NonExistentKey_ReturnsKey()
        {
            // Arrange
            var method = typeof(Resources).GetMethod("GetDefaultResourceString", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            // Act
            var result = method?.Invoke(null, new[] { "SomeRandomKey" }) as string;
            
            // Assert
            Assert.NotNull(result);
            Assert.Equal("SomeRandomKey", result);
        }

        [Fact]
        public void GetDefaultResourceString_KnownKeys_ReturnsFallbackValues()
        {
            // Arrange
            var method = typeof(Resources).GetMethod("GetDefaultResourceString", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            // Act & Assert
            var keys = new[]
            {
                "MemberNotFoundTitle",
                "MemberNotFoundMessageFormat",
                "MemberNotFoundDescription",
                "VariableNotFoundTitle",
                "VariableNotFoundMessageFormat",
                "VariableNotFoundDescription",
                "NamespaceNotFoundTitle",
                "NamespaceNotFoundMessageFormat",
                "NamespaceNotFoundDescription",
                "NamedArgumentNotFoundTitle",
                "NamedArgumentNotFoundMessageFormat",
                "NamedArgumentNotFoundDescription"
            };
            
            foreach (var key in keys)
            {
                var result = method?.Invoke(null, new[] { key }) as string;
                Assert.NotNull(result);
                Assert.NotEqual(key, result); // Should not return the key itself for known keys
            }
        }

        [Fact]
        public void GetStringTwice_GivesSameResult_AndCaches()
        {
            // Проверяем, что получение строки дважды возвращает одинаковый результат
            // что подразумевает работу механизма кэширования
            
            // Arrange - используем рефлексию для доступа к внутренним методам
            var getStringMethod = typeof(Resources).GetMethod("GetString",
                BindingFlags.NonPublic | BindingFlags.Static);
            
            // Act
            var result1 = getStringMethod.Invoke(null, new[] { "MemberNotFoundTitle" }) as string;
            var result2 = getStringMethod.Invoke(null, new[] { "MemberNotFoundTitle" }) as string;
            
            // Assert
            Assert.NotNull(result1);
            Assert.Equal(result1, result2);
        }

        [Fact]
        public void ResourceManager_ReinitializationReturnsCache()
        {
            // Проверяем, что повторный вызов ResourceManager 
            // возвращает закешированный экземпляр
            
            // Act - получаем экземпляр дважды
            var resourceManager1 = Resources.ResourceManager;
            var resourceManager2 = Resources.ResourceManager;
            
            // Assert
            Assert.NotNull(resourceManager1);
            Assert.Same(resourceManager1, resourceManager2); // Должен быть тот же экземпляр
        }

        [Fact]
        public void GetResourceString_ForAllProperties_MatchesDirectAccess()
        {
            // Arrange - получаем приватный метод через рефлексию
            var getStringMethod = typeof(Resources).GetMethod("GetString",
                BindingFlags.NonPublic | BindingFlags.Static);
            
            // Список всех свойств ресурсов
            var resourceProperties = typeof(Resources)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(string))
                .ToList();
            
            foreach (var property in resourceProperties)
            {
                // Act - два способа получения строки ресурса
                var directValue = property.GetValue(null) as string;
                var indirectValue = getStringMethod.Invoke(null, new[] { property.Name }) as string;
                
                // Assert
                Assert.NotNull(directValue);
                Assert.Equal(directValue, indirectValue);
            }
        }
    }
} 