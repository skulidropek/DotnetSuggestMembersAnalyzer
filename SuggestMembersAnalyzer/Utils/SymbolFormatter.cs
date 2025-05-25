// <copyright file="SymbolFormatter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Microsoft.CodeAnalysis;

    /// <summary>
    /// Utility methods for formatting Roslyn symbols and raw type strings.
    /// </summary>
    internal static class SymbolFormatter
    {
        private const string AttributeSuffix = nameof(Attribute);
        private const int PropertyPrefixLength = 4;
        private static Compilation? currentCompilation;

        /// <summary>
        /// Initializes the formatter with the current compilation context.
        /// Call this at the beginning of analysis to enable enhanced attribute formatting.
        /// </summary>
        /// <param name="compilation">The compilation context to use.</param>
        /// <exception cref="ArgumentNullException">Thrown when compilation is null.</exception>
        internal static void Initialize(Compilation compilation)
        {
            currentCompilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        }

        /// <summary>
        /// Formats any entry (ISymbol or string) with a [Kind] prefix.
        /// </summary>
        /// <param name="entry">The entry to format (ISymbol or string).</param>
        /// <returns>Formatted string with kind prefix.</returns>
        /// <exception cref="ArgumentNullException">Thrown when entry is null.</exception>
        internal static string FormatAny(object entry)
        {
            return entry is null
                ? throw new ArgumentNullException(nameof(entry))
                : entry switch
                {
                    ISymbol sym => FormatSymbol(sym),
                    string s when s.EndsWith(AttributeSuffix, StringComparison.Ordinal)
                                                                          => FormatAttributeFromString(s),
                    string s => $"[Class]     {s}",
                    _ => entry.ToString() ?? string.Empty,
                };
        }

        /// <summary>
        /// Formats a Roslyn symbol with kind tag and minimal signature.
        /// </summary>
        /// <param name="symbol">The symbol to format.</param>
        /// <returns>Formatted string with kind tag and signature.</returns>
        /// <exception cref="ArgumentNullException">Thrown when symbol is null.</exception>
        internal static string FormatSymbol(ISymbol symbol)
        {
            return symbol is null
                ? throw new ArgumentNullException(nameof(symbol))
                : symbol switch
                {
                    IMethodSymbol m when IsAttributeCtor(m) => FormatAttributeCtor(m),
                    IMethodSymbol m => FormatMethod(m),
                    IPropertySymbol p => FormatProperty(p),
                    IFieldSymbol f => FormatField(f),
                    ILocalSymbol l => $"[Local]     {FormatType(l.Type)} {l.Name}",
                    IParameterSymbol p => $"[Parameter] {FormatType(p.Type)} {p.Name}",
                    INamedTypeSymbol t when IsAttributeType(t) => FormatAttributeType(t),
                    INamedTypeSymbol t => $"[Class]     {t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty)}",
                    _ => symbol.ToDisplayString(),
                };
        }

        /// <summary>Returns a readable kind name for diagnostics.</summary>
        /// <param name="entry">The entry to get kind for.</param>
        /// <returns>Readable kind name.</returns>
        /// <exception cref="ArgumentNullException">Thrown when entry is null.</exception>
        internal static string GetEntityKind(object entry)
        {
            return entry is null
                ? throw new ArgumentNullException(nameof(entry))
                : entry switch
                {
                    INamedTypeSymbol t when IsAttributeType(t) => "Attribute",
                    IMethodSymbol m when IsAttributeCtor(m) => "Attribute",
                    IMethodSymbol => "Method",
                    IPropertySymbol => "Property",
                    IFieldSymbol => "Field",
                    ILocalSymbol => "Local",
                    IParameterSymbol => "Parameter",
                    string s when s.EndsWith(AttributeSuffix, StringComparison.Ordinal) => "Attribute",
                    string => "Class",
                    _ => "Identifier",
                };
        }

        /// <summary>
        /// Returns member name or full signature depending on <paramref name="includeSignature"/>.
        /// </summary>
        /// <param name="member">The symbol member to format.</param>
        /// <param name="includeSignature">Whether to include full signature or just name.</param>
        /// <returns>Formatted member representation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when member is null.</exception>
        internal static string GetFormattedMemberRepresentation(this ISymbol member, bool includeSignature)
        {
            return member is null
                ? throw new ArgumentNullException(nameof(member))
                : !includeSignature
                ? member.Name
                : member switch
                {
                    IMethodSymbol m => GetMethodSignature(m),
                    IPropertySymbol p => GetPropertySignature(p),
                    IFieldSymbol f => GetFieldSignature(f),
                    _ => member.Name,
                };
        }

        /// <summary>
        /// Gets the formatted display name of a type symbol.
        /// </summary>
        /// <param name="type">The type symbol to format.</param>
        /// <returns>Formatted type name string.</returns>
        internal static string GetFormattedTypeName(this ITypeSymbol type)
        {
            if (type is null)
            {
                return "object";
            }

            SymbolDisplayFormat fmt = SymbolDisplayFormat.MinimallyQualifiedFormat
                .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes)
                .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters);

            return type.ToDisplayString(fmt);
        }

        // ------------------------------------------------------------------
        //  Signature utilities (used in other files)
        // ------------------------------------------------------------------

        /// <summary>
        /// Gets the formatted signature of a method symbol.
        /// </summary>
        /// <param name="method">The method symbol to format.</param>
        /// <returns>Formatted method signature string.</returns>
        /// <exception cref="ArgumentNullException">Thrown when method is null.</exception>
        internal static string GetMethodSignature(this IMethodSymbol method)
        {
            if (method is null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            try
            {
                // Handle property accessors (getters and setters)
                string? propertySignature = TryFormatAsPropertyAccessor(method);
                if (propertySignature != null)
                {
                    return propertySignature;
                }

                // Format as regular method
                return FormatRegularMethodSignature(method);
            }
            catch (Exception ex)
            {
                // Log detailed error information for SuggestMembersAnalyzer
                System.Diagnostics.Debug.WriteLine(
                    "[SuggestMembersAnalyzer] SymbolFormatter.GetMethodSignature failed for method " +
                    $"'{method.Name}' in type '{method.ContainingType?.Name}': {ex}");

                // Fallback to simple name if signature generation fails
                return $"{method.Name}()";
            }
        }

        /// <summary>
        /// Gets the formatted signature of a property symbol.
        /// </summary>
        /// <param name="property">The property symbol to format.</param>
        /// <returns>Formatted property signature string.</returns>
        /// <exception cref="ArgumentNullException">Thrown when property is null.</exception>
        internal static string GetPropertySignature(this IPropertySymbol property)
        {
            if (property is null)
            {
                throw new ArgumentNullException(nameof(property));
            }

            try
            {
                StringBuilder sb = new();
                if (property.IsStatic)
                {
                    sb.Append("static ");
                }

                if (property.IsAbstract)
                {
                    sb.Append("abstract ");
                }

                if (property.IsVirtual)
                {
                    sb.Append("virtual ");
                }

                if (property.IsOverride)
                {
                    sb.Append("override ");
                }

                sb.Append(GetFormattedTypeName(property.Type))
                  .Append(' ')
                  .Append(property.Name)
                  .Append(" { ");

                if (property.GetMethod != null)
                {
                    sb.Append("get; ");
                }

                if (property.SetMethod != null)
                {
                    sb.Append("set; ");
                }

                sb.Append('}');
                return sb.ToString();
            }
            catch (Exception ex)
            {
                // Log detailed error information for SuggestMembersAnalyzer
                System.Diagnostics.Debug.WriteLine(
                    "[SuggestMembersAnalyzer] SymbolFormatter.GetPropertySignature failed for property " +
                    $"'{property.Name}' in type '{property.ContainingType?.Name}': {ex}");

                // Fallback to simple name if signature generation fails
                return property.Name;
            }
        }

        /// <summary>
        /// Formats an attribute constructor for display in diagnostics.
        /// </summary>
        /// <param name="ctor">The constructor method symbol to format.</param>
        /// <returns>A formatted string representation of the attribute constructor.</returns>
        private static string FormatAttributeCtor(IMethodSymbol ctor)
        {
            string parameters = string.Join(
                ", ",
                ctor.Parameters.Select(static p => $"{FormatType(p.Type)} {p.Name}"));
            string owner = ctor.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                          .Replace("global::", string.Empty);
            return $"[Attribute] {owner}({parameters})";
        }

        private static string FormatAttributeFromString(string fullName)
        {
            if (currentCompilation is null)
            {
                return $"[Attribute] {fullName}";
            }

            // Try to find the type in the compilation
            INamedTypeSymbol? type = currentCompilation.GetTypeByMetadataName(fullName);
            if (type != null)
            {
                return FormatAttributeType(type);
            }

            // If type not found, try alternate names (with/without Attribute suffix)
            if (fullName.EndsWith(AttributeSuffix, StringComparison.Ordinal))
            {
                string shortName = fullName.Substring(0, fullName.Length - AttributeSuffix.Length);
                type = currentCompilation.GetTypeByMetadataName(shortName);
                if (type != null)
                {
                    return FormatAttributeType(type);
                }
            }
            else
            {
                string longName = fullName + AttributeSuffix;
                type = currentCompilation.GetTypeByMetadataName(longName);
                if (type != null)
                {
                    return FormatAttributeType(type);
                }
            }

            // If still not found, use basic formatting
            return $"[Attribute] {fullName}()";
        }

        /// <summary>
        /// Formats an attribute type with its constructors for display in diagnostics.
        /// </summary>
        /// <param name="t">The attribute type symbol to format.</param>
        /// <returns>A formatted string representation of the attribute.</returns>
        private static string FormatAttributeType(INamedTypeSymbol t)
        {
            // Get all public constructors
            List<IMethodSymbol> ctors = [.. t.InstanceConstructors.Where(static c => c is { DeclaredAccessibility: Accessibility.Public, IsStatic: false })];

            if (ctors.Count == 0)
            {
                // If no constructors available, just output the type name
                return $"[Attribute] {t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty)}";
            }

            if (ctors.Count == 1)
            {
                // If only one constructor, use single formatter
                return FormatAttributeCtor(ctors[0]);
            }

            // If multiple constructors, display each on a separate line
            string typeName = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            StringBuilder builder = new();

            builder.Append("[Attribute] ").Append(typeName).AppendLine(" - available constructors:");
            foreach (IMethodSymbol? ctor in ctors)
            {
                string parameters = string.Join(
                    ", ",
                    ctor.Parameters.Select(static p => $"{FormatType(p.Type)} {p.Name}"));
                builder.Append("  • ").Append(typeName).Append('(').Append(parameters).Append(')').AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private static string FormatField(IFieldSymbol f)
        {
            string typ = FormatType(f.Type);
            string own = f.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            return $"[Field]     {typ} {own}.{f.Name}";
        }

        /// <summary>
        /// Formats a method symbol for display in diagnostics.
        /// </summary>
        /// <param name="m">The method symbol to format.</param>
        /// <returns>A formatted string representation of the method.</returns>
        private static string FormatMethod(IMethodSymbol m)
        {
            string ret = FormatType(m.ReturnType);
            string pars = string.Join(", ", m.Parameters.Select(static p => $"{FormatType(p.Type)} {p.Name}"));
            string own = m.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            return $"[Method]    {ret} {own}.{m.Name}({pars})";
        }

        /// <summary>
        /// Formats method parameters for signature display.
        /// </summary>
        /// <param name="method">The method whose parameters to format.</param>
        /// <returns>Formatted parameters string.</returns>
        private static string FormatMethodParameters(IMethodSymbol method)
        {
            return string.Join(", ", method.Parameters.Select(static p =>
            {
                string mod = p.RefKind switch
                {
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    RefKind.In => "in ",
                    RefKind.None => string.Empty,
                    _ => string.Empty,
                };
                return $"{mod}{GetFormattedTypeName(p.Type)} {p.Name}";
            }));
        }

        private static string FormatProperty(IPropertySymbol p)
        {
            string typ = FormatType(p.Type);
            string own = p.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            const string acc = "{ get; }";
            return $"[Property]  {typ} {own}.{p.Name} {acc}";
        }

        /// <summary>
        /// Formats a regular method signature.
        /// </summary>
        /// <param name="method">The method to format.</param>
        /// <returns>Formatted method signature.</returns>
        private static string FormatRegularMethodSignature(IMethodSymbol method)
        {
            StringBuilder sb = new();

            // Return type
            sb.Append(GetFormattedTypeName(method.ReturnType)).Append(' ');

            // Extension method prefix
            if (method.IsExtensionMethod || method.MethodKind == MethodKind.ReducedExtension)
            {
                sb.Append(method.ContainingType.ToDisplayString()).Append('.');
            }

            // Method name
            sb.Append(method.Name);

            // Generic type parameters
            if (method is { IsGenericMethod: true } && method.TypeParameters.Length > 0)
            {
                sb.Append('<')
                  .Append(string.Join(", ", method.TypeParameters.Select(static tp => tp.Name)))
                  .Append('>');
            }

            // Parameters
            sb.Append('(').Append(FormatMethodParameters(method)).Append(')');

            return sb.ToString();
        }

        private static string FormatType(ITypeSymbol t)
        {
            return t is null
                ? "object" : t.SpecialType switch
                {
                    SpecialType.System_Void or SpecialType.System_Boolean or SpecialType.System_Char or
                    SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or
                    SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or
                    SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Decimal or
                    SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_String or
                    SpecialType.System_Object
                        => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    SpecialType.None => throw new NotSupportedException(),
                    SpecialType.System_Enum => throw new NotSupportedException(),
                    SpecialType.System_MulticastDelegate => throw new NotSupportedException(),
                    SpecialType.System_Delegate => throw new NotSupportedException(),
                    SpecialType.System_ValueType => throw new NotSupportedException(),
                    SpecialType.System_IntPtr => throw new NotSupportedException(),
                    SpecialType.System_UIntPtr => throw new NotSupportedException(),
                    SpecialType.System_Array => throw new NotSupportedException(),
                    SpecialType.System_Collections_IEnumerable => throw new NotSupportedException(),
                    SpecialType.System_Collections_Generic_IEnumerable_T => throw new NotSupportedException(),
                    SpecialType.System_Collections_Generic_IList_T => throw new NotSupportedException(),
                    SpecialType.System_Collections_Generic_ICollection_T => throw new NotSupportedException(),
                    SpecialType.System_Collections_IEnumerator => throw new NotSupportedException(),
                    SpecialType.System_Collections_Generic_IEnumerator_T => throw new NotSupportedException(),
                    SpecialType.System_Collections_Generic_IReadOnlyList_T => throw new NotSupportedException(),
                    SpecialType.System_Collections_Generic_IReadOnlyCollection_T => throw new NotSupportedException(),
                    SpecialType.System_Nullable_T => throw new NotSupportedException(),
                    SpecialType.System_DateTime => throw new NotSupportedException(),
                    SpecialType.System_Runtime_CompilerServices_IsVolatile => throw new NotSupportedException(),
                    SpecialType.System_IDisposable => throw new NotSupportedException(),
                    SpecialType.System_TypedReference => throw new NotSupportedException(),
                    SpecialType.System_ArgIterator => throw new NotSupportedException(),
                    SpecialType.System_RuntimeArgumentHandle => throw new NotSupportedException(),
                    SpecialType.System_RuntimeFieldHandle => throw new NotSupportedException(),
                    SpecialType.System_RuntimeMethodHandle => throw new NotSupportedException(),
                    SpecialType.System_RuntimeTypeHandle => throw new NotSupportedException(),
                    SpecialType.System_IAsyncResult => throw new NotSupportedException(),
                    SpecialType.System_AsyncCallback => throw new NotSupportedException(),
                    SpecialType.System_Runtime_CompilerServices_RuntimeFeature => throw new NotSupportedException(),
                    SpecialType.System_Runtime_CompilerServices_PreserveBaseOverridesAttribute => throw new NotSupportedException(),
                    SpecialType.System_Runtime_CompilerServices_InlineArrayAttribute => throw new NotSupportedException(),
                    _ => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty),
                };
        }

        private static string GetFieldSignature(IFieldSymbol f)
        {
            string staticModifier = f.IsStatic ? "static " : string.Empty;
            return $"{staticModifier}{GetFormattedTypeName(f.Type)} {f.Name}";
        }

        private static bool IsAttributeCtor(IMethodSymbol m)
        {
            return m is { MethodKind: MethodKind.Constructor } && IsAttributeType(m.ContainingType);
        }

        /// <summary>
        /// Determines whether the specified type symbol is an attribute type.
        /// </summary>
        /// <param name="t">The type symbol to check.</param>
        /// <returns>True if the type is an attribute type; otherwise, false.</returns>
        private static bool IsAttributeType(ITypeSymbol t)
        {
            return string.Equals(t?.BaseType?.ToDisplayString(), "System.Attribute", StringComparison.Ordinal);
        }

        /// <summary>
        /// Tries to format a method as a property accessor (getter/setter).
        /// </summary>
        /// <param name="method">The method to check.</param>
        /// <returns>Property signature if it's an accessor, null otherwise.</returns>
        private static string? TryFormatAsPropertyAccessor(IMethodSymbol method)
        {
            if (method is { MethodKind: MethodKind.PropertyGet } && method.Name.StartsWith("get_", StringComparison.Ordinal))
            {
                string name = method.Name.Substring(PropertyPrefixLength);
                IPropertySymbol prop = method.ContainingType.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
                return prop != null
                    ? GetPropertySignature(prop)
                    : $"{GetFormattedTypeName(method.ReturnType)} {name} {{ get; }}";
            }

            if (method is { MethodKind: MethodKind.PropertySet } && method.Name.StartsWith("set_", StringComparison.Ordinal))
            {
                string name = method.Name.Substring(PropertyPrefixLength);
                IPropertySymbol prop = method.ContainingType.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
                if (prop != null)
                {
                    return GetPropertySignature(prop);
                }

                ITypeSymbol pType = method.Parameters.FirstOrDefault()?.Type ?? method.ContainingType;
                return $"{GetFormattedTypeName(pType)} {name} {{ set; }}";
            }

            return null;
        }
    }
}
