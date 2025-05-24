// <copyright file="SymbolFormatter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SuggestMembersAnalyzer.Utils
{
    using System;
    using System.Linq;
    using System.Text;
    using Microsoft.CodeAnalysis;

    /// <summary>
    /// Utility methods for formatting Roslyn symbols and raw type strings.
    /// </summary>
    internal static class SymbolFormatter
    {
        private const string AttributeSuffix = "Attribute";
        private static Compilation? currentCompilation;

        /// <summary>
        /// Initializes the formatter with the current compilation context.
        /// Call this at the beginning of analysis to enable enhanced attribute formatting.
        /// </summary>
        /// <param name="compilation">The compilation context to use.</param>
        internal static void Initialize(Compilation compilation)
        {
            currentCompilation = compilation;
        }

        // ------------------------------------------------------------------
        //  Main internal helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats any entry (ISymbol or string) with a [Kind] prefix.
        /// </summary>
        /// <param name="entry">The entry to format (ISymbol or string).</param>
        /// <returns>Formatted string with kind prefix.</returns>
        internal static string FormatAny(object entry) => entry switch
        {
            ISymbol sym => FormatSymbol(sym),
            string s when s.EndsWith(AttributeSuffix, StringComparison.Ordinal)
                                                                  => FormatAttributeFromString(s),
            string s => "[Class]     " + s,
            _ => entry?.ToString() ?? string.Empty
        };

        /// <summary>
        /// Formats a Roslyn symbol with kind tag and minimal signature.
        /// </summary>
        /// <param name="symbol">The symbol to format.</param>
        /// <returns>Formatted string with kind tag and signature.</returns>
        internal static string FormatSymbol(ISymbol symbol) => symbol switch
        {
            IMethodSymbol m when IsAttributeCtor(m) => FormatAttributeCtor(m),
            IMethodSymbol m => FormatMethod(m),
            IPropertySymbol p => FormatProperty(p),
            IFieldSymbol f => FormatField(f),
            ILocalSymbol l => $"[Local]     {FormatType(l.Type)} {l.Name}",
            IParameterSymbol p => $"[Parameter] {FormatType(p.Type)} {p.Name}",
            INamedTypeSymbol t when IsAttributeType(t) => FormatAttributeType(t),
            INamedTypeSymbol t => "[Class]     " +
                                                                    t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                                                     .Replace("global::", string.Empty),
            _ => symbol.ToDisplayString()
        };

        /// <summary>Returns a readable kind name for diagnostics.</summary>
        /// <param name="entry">The entry to get kind for.</param>
        /// <returns>Readable kind name.</returns>
        internal static string GetEntityKind(object entry) => entry switch
        {
            INamedTypeSymbol t when IsAttributeType(t) => "Attribute",
            IMethodSymbol m when IsAttributeCtor(m) => "Attribute",
            IMethodSymbol => "Method",
            IPropertySymbol => "Property",
            IFieldSymbol => "Field",
            ILocalSymbol => "Local",
            IParameterSymbol => "Parameter",
            string s when s.EndsWith(AttributeSuffix, StringComparison.Ordinal)
                                                             => "Attribute",
            string => "Class",
            _ => "Identifier"
        };

        // ------------------------------------------------------------------
        //  Signature utilities (used in other files)
        // ------------------------------------------------------------------

        /// <summary>
        /// Gets the formatted signature of a method symbol.
        /// </summary>
        /// <param name="method">The method symbol to format.</param>
        /// <returns>Formatted method signature string.</returns>
        internal static string GetMethodSignature(this IMethodSymbol method)
        {
            try
            {
                // --- process getters and setters as properties ---
                if (method.MethodKind == MethodKind.PropertyGet && method.Name.StartsWith("get_"))
                {
                    var name = method.Name.Substring(4);
                    var prop = method.ContainingType.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
                    return prop != null
                        ? GetPropertySignature(prop)
                        : $"{GetFormattedTypeName(method.ReturnType)} {name} {{ get; }}";
                }

                if (method.MethodKind == MethodKind.PropertySet && method.Name.StartsWith("set_"))
                {
                    var name = method.Name.Substring(4);
                    var prop = method.ContainingType.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
                    if (prop != null)
                    {
                        return GetPropertySignature(prop);
                    }

                    var pType = method.Parameters.FirstOrDefault()?.Type ?? method.ContainingType;
                    return $"{GetFormattedTypeName(pType)} {name} {{ set; }}";
                }

                // --- regular method ---
                var sb = new StringBuilder();
                sb.Append(GetFormattedTypeName(method.ReturnType)).Append(' ');

                if (method.IsExtensionMethod || method.MethodKind == MethodKind.ReducedExtension)
                {
                    sb.Append(method.ContainingType.ToDisplayString()).Append('.');
                }

                sb.Append(method.Name);

                if (method.IsGenericMethod && method.TypeParameters.Length > 0)
                {
                    sb.Append('<')
                    .Append(string.Join(", ", method.TypeParameters.Select(tp => tp.Name)))
                    .Append('>');
                }

                sb.Append('(')
                .Append(string.Join(", ", method.Parameters.Select(p =>
                {
                    var mod = p.RefKind switch
                    {
                        RefKind.Ref => "ref ",
                        RefKind.Out => "out ",
                        RefKind.In => "in ",
                        _ => string.Empty
                    };
                    return $"{mod}{GetFormattedTypeName(p.Type)} {p.Name}";
                })))
                .Append(')');

                return sb.ToString();
            }
            catch (Exception ex)
            {
                // Log detailed error information for SuggestMembersAnalyzer
                System.Diagnostics.Debug.WriteLine($"[SuggestMembersAnalyzer] SymbolFormatter.GetMethodSignature failed for method '{method.Name}' in type '{method.ContainingType?.Name}': {ex}");

                // Fallback to simple name if signature generation fails
                return method.Name + "()";
            }
        }

        /// <summary>
        /// Gets the formatted signature of a property symbol.
        /// </summary>
        /// <param name="property">The property symbol to format.</param>
        /// <returns>Formatted property signature string.</returns>
        internal static string GetPropertySignature(this IPropertySymbol property)
        {
            try
            {
                var sb = new StringBuilder();
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
                System.Diagnostics.Debug.WriteLine($"[SuggestMembersAnalyzer] SymbolFormatter.GetPropertySignature failed for property '{property.Name}' in type '{property.ContainingType?.Name}': {ex}");

                // Fallback to simple name if signature generation fails
                return property.Name;
            }
        }

        /// <summary>
        /// Returns member name or full signature depending on <paramref name="includeSignature"/>.
        /// </summary>
        /// <param name="member">The symbol member to format.</param>
        /// <param name="includeSignature">Whether to include full signature or just name.</param>
        /// <returns>Formatted member representation.</returns>
        internal static string GetFormattedMemberRepresentation(this ISymbol member, bool includeSignature)
        {
            if (!includeSignature)
            {
                return member.Name;
            }

            return member switch
            {
                IMethodSymbol m => GetMethodSignature(m),
                IPropertySymbol p => GetPropertySignature(p),
                IFieldSymbol f => $"{(f.IsStatic ? "static " : string.Empty)}{GetFormattedTypeName(f.Type)} {f.Name}",
                _ => member.Name
            };
        }

        /// <summary>
        /// Gets the formatted display name of a type symbol.
        /// </summary>
        /// <param name="type">The type symbol to format.</param>
        /// <returns>Formatted type name string.</returns>
        internal static string GetFormattedTypeName(this ITypeSymbol type)
        {
            if (type == null)
            {
                return "object";
            }

            var fmt = SymbolDisplayFormat.MinimallyQualifiedFormat
                .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes)
                .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters);

            return type.ToDisplayString(fmt);
        }

        /// <summary>
        /// Formats an attribute type with its constructors for display in diagnostics.
        /// </summary>
        /// <param name="t">The attribute type symbol to format.</param>
        /// <returns>A formatted string representation of the attribute.</returns>
        private static string FormatAttributeType(INamedTypeSymbol t)
        {
            // Get all public constructors
            var ctors = t.InstanceConstructors
                          .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsStatic)
                          .ToList();

            if (ctors.Count == 0)
            {
                // If no constructors available, just output the type name
                return $"[Attribute] {t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty)}";
            }
            else if (ctors.Count == 1)
            {
                // If only one constructor, use single formatter
                return FormatAttributeCtor(ctors[0]);
            }
            else
            {
                // If multiple constructors, display each on a separate line
                var typeName = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
                var builder = new StringBuilder();

                builder.AppendLine($"[Attribute] {typeName} - available constructors:");
                foreach (var ctor in ctors)
                {
                    var parameters = string.Join(
                        ", ",
                        ctor.Parameters.Select(p => $"{FormatType(p.Type)} {p.Name}"));
                    builder.AppendLine($"  • {typeName}({parameters})");
                }

                return builder.ToString().TrimEnd();
            }
        }

        /// <summary>
        /// Formats an attribute constructor for display in diagnostics.
        /// </summary>
        /// <param name="ctor">The constructor method symbol to format.</param>
        /// <returns>A formatted string representation of the attribute constructor.</returns>
        private static string FormatAttributeCtor(IMethodSymbol ctor)
        {
            var parameters = string.Join(
                ", ",
                ctor.Parameters.Select(p => $"{FormatType(p.Type)} {p.Name}"));
            var owner = ctor.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                          .Replace("global::", string.Empty);
            return $"[Attribute] {owner}({parameters})";
        }

        /// <summary>
        /// Formats a method symbol for display in diagnostics.
        /// </summary>
        /// <param name="m">The method symbol to format.</param>
        /// <returns>A formatted string representation of the method.</returns>
        private static string FormatMethod(IMethodSymbol m)
        {
            var ret = FormatType(m.ReturnType);
            var pars = string.Join(", ", m.Parameters.Select(p => $"{FormatType(p.Type)} {p.Name}"));
            var own = m.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            return $"[Method]    {ret} {own}.{m.Name}({pars})";
        }

        private static string FormatProperty(IPropertySymbol p)
        {
            var typ = FormatType(p.Type);
            var own = p.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            var acc = p.SetMethod != null ? "{ get; set; }" : "{ get; }";
            return $"[Property]  {typ} {own}.{p.Name} {acc}";
        }

        private static string FormatField(IFieldSymbol f)
        {
            var typ = FormatType(f.Type);
            var own = f.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            return $"[Field]     {typ} {own}.{f.Name}";
        }

        private static string FormatType(ITypeSymbol t)
        {
            if (t == null)
            {
                return "object";
            }

            return t.SpecialType switch
            {
                SpecialType.System_Void or SpecialType.System_Boolean or SpecialType.System_Char or
                SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or
                SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or
                SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Decimal or
                SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_String or
                SpecialType.System_Object
                    => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),

                _ => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty)
            };
        }

        // ------------------------------------------------------------------
        //  Helper predicates
        // ------------------------------------------------------------------
        private static bool IsAttributeType(ITypeSymbol t) =>
            t?.BaseType?.ToDisplayString() == "System.Attribute";

        private static bool IsAttributeCtor(IMethodSymbol m) =>
            m.MethodKind == MethodKind.Constructor && IsAttributeType(m.ContainingType);

        private static string FormatAttributeFromString(string fullName)
        {
            if (currentCompilation == null)
            {
                return $"[Attribute] {fullName}";
            }

            // Try to find the type in the compilation
            var type = currentCompilation.GetTypeByMetadataName(fullName);
            if (type != null)
            {
                return FormatAttributeType(type);
            }

            // If type not found, try alternate names (with/without Attribute suffix)
            if (fullName.EndsWith(AttributeSuffix, StringComparison.Ordinal))
            {
                var shortName = fullName.Substring(0, fullName.Length - AttributeSuffix.Length);
                type = currentCompilation.GetTypeByMetadataName(shortName);
                if (type != null)
                {
                    return FormatAttributeType(type);
                }
            }
            else
            {
                var longName = fullName + AttributeSuffix;
                type = currentCompilation.GetTypeByMetadataName(longName);
                if (type != null)
                {
                    return FormatAttributeType(type);
                }
            }

            // If still not found, use basic formatting
            return $"[Attribute] {fullName}()";
        }
    }
}
