using Microsoft.CodeAnalysis;
using System.Linq;
using System.Text;

namespace SuggestMembersAnalyzer.Utils
{
    /// <summary>
    /// Utility methods for formatting and displaying symbol information
    /// </summary>
    internal static class SymbolFormatter
    {
        /// <summary>
        /// Determines the entity kind based on the type of symbol
        /// </summary>
        public static string GetEntityKind(object entry)
        {
            return entry switch
            {
                IMethodSymbol    => "Method",
                IPropertySymbol  => "Property",
                IFieldSymbol     => "Field",
                ILocalSymbol     => "Local",
                IParameterSymbol => "Parameter",
                string           => "Class",
                _                => "Identifier"
            };
        }

        /// <summary>
        /// Formats type information based on special type or custom type
        /// </summary>
        public static string FormatType(ITypeSymbol t)
        {
            return t.SpecialType switch
            {
                SpecialType.System_Void    or
                SpecialType.System_Boolean or
                SpecialType.System_Char    or
                SpecialType.System_SByte   or
                SpecialType.System_Byte    or
                SpecialType.System_Int16   or
                SpecialType.System_UInt16  or
                SpecialType.System_Int32   or
                SpecialType.System_UInt32  or
                SpecialType.System_Int64   or
                SpecialType.System_UInt64  or
                SpecialType.System_Decimal or
                SpecialType.System_Single  or
                SpecialType.System_Double  or
                SpecialType.System_String  or
                SpecialType.System_Object
                    => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                _ => t
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    .Replace("global::", "")
            };
        }
        
        /// <summary>
        /// Formats a symbol with appropriate prefix label based on its type
        /// </summary>
        public static string FormatSymbol(ISymbol symbol)
        {
            switch (symbol)
            {
                case IMethodSymbol m:
                {
                    string ret = FormatType(m.ReturnType);
                    string @params = string.Join(", ",
                        m.Parameters.Select(p => $"{FormatType(p.Type)} {p.Name}"));
                    string owner = m.ContainingType
                                   .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                   .Replace("global::", "");
                    return $"[Method] {ret} {owner}.{m.Name}({@params})";
                }
                case IPropertySymbol p:
                {
                    string typ = FormatType(p.Type);
                    string acc = p.SetMethod != null ? "{ get; set; }" : "{ get; }";
                    string owner = p.ContainingType
                                   .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                   .Replace("global::", "");
                    return $"[Property] {typ} {owner}.{p.Name} {acc}";
                }
                case IFieldSymbol f:
                {
                    string typ = FormatType(f.Type);
                    string owner = f.ContainingType
                                   .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                   .Replace("global::", "");
                    return $"[Field] {typ} {owner}.{f.Name}";
                }
                case ILocalSymbol l:
                    return $"[Local] {FormatType(l.Type)} {l.Name}";
                case IParameterSymbol p:
                    return $"[Parameter] {FormatType(p.Type)} {p.Name}";
                case INamedTypeSymbol t:
                    return "[Class] " + t
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        .Replace("global::", "");
                default:
                    return symbol.ToDisplayString();
            }
        }

        /// <summary>
        /// Gets a formatted signature for a method,
        /// and for extension methods includes the namespace and containing static class.
        /// </summary>
        public static string GetMethodSignature(this IMethodSymbol method)
        {
            try
            {
                // 1) Property getters/setters — unchanged
                if (method.MethodKind == MethodKind.PropertyGet && method.Name.StartsWith("get_"))
                {
                    string prop = method.Name.Substring(4);
                    var psym = method.ContainingType
                                    .GetMembers(prop)
                                    .OfType<IPropertySymbol>()
                                    .FirstOrDefault();
                    return psym != null
                        ? GetPropertySignature(psym)
                        : $"{GetFormattedTypeName(method.ReturnType)} {prop} {{ get; }}";
                }

                if (method.MethodKind == MethodKind.PropertySet && method.Name.StartsWith("set_"))
                {
                    string prop = method.Name.Substring(4);
                    var psym = method.ContainingType
                                    .GetMembers(prop)
                                    .OfType<IPropertySymbol>()
                                    .FirstOrDefault();
                    if (psym != null)
                            {
                                return GetPropertySignature(psym);
                            }

                            var ptype = method.Parameters.FirstOrDefault()?.Type ?? method.ContainingType;
                    return $"{GetFormattedTypeName(ptype)} {prop} {{ set; }}";
                }

                // 2) Start building the signature
                var sb = new StringBuilder();

                // 2.1) Return type
                sb.Append(GetFormattedTypeName(method.ReturnType))
                .Append(' ');

                // 2.2) For extension methods add prefix "Namespace.Class."
                if (method.IsExtensionMethod || method.MethodKind == MethodKind.ReducedExtension)
                {
                    // ContainingType.ToDisplayString() gives "System.Linq.Enumerable" etc.
                    var container = method.ContainingType.ToDisplayString();
                    sb.Append(container).Append('.');
                }

                // 2.3) Method name + generic parameters
                sb.Append(method.Name);
                if (method.IsGenericMethod && method.TypeParameters.Length > 0)
                {
                    sb.Append('<')
                    .Append(string.Join(", ", method.TypeParameters.Select(tp => tp.Name)))
                    .Append('>');
                }

                // 2.4) Parameters
                sb.Append('(')
                .Append(string.Join(", ", method.Parameters.Select(p =>
                {
                    var mod = p.RefKind switch
                    {
                        RefKind.Ref => "ref ",
                        RefKind.Out => "out ",
                        RefKind.In  => "in ",
                        _           => ""
                    };
                    return $"{mod}{GetFormattedTypeName(p.Type)} {p.Name}";
                })))
                .Append(')');

                return sb.ToString();
            }
            catch
            {
                // fallback
                return method.Name + "()";
            }
        }

        /// <summary>
        /// Gets a formatted signature for a property
        /// </summary>
        public static string GetPropertySignature(this IPropertySymbol property)
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
            catch
            {
                return property.Name;
            }
        }

        /// <summary>
        /// Gets a formatted representation of a member, optionally including its signature
        /// </summary>
        public static string GetFormattedMemberRepresentation(this ISymbol member, bool includeSignature)
        {
            if (!includeSignature)
            {
                return member.Name;
            }

            try
            {
                return member switch
                {
                    IMethodSymbol   m => GetMethodSignature(m),
                    IPropertySymbol p => GetPropertySignature(p),
                    IFieldSymbol    f => $"{(f.IsStatic ? "static " : "")}{GetFormattedTypeName(f.Type)} {f.Name}",
                    _                  => member.Name
                };
            }
            catch
            {
                return member.Name;
            }
        }

        /// <summary>
        /// Gets a formatted type name
        /// </summary>
        public static string GetFormattedTypeName(this ITypeSymbol type)
        {
            if (type == null)
            {
                return "object";
            }

            var fmt = SymbolDisplayFormat.MinimallyQualifiedFormat
                .WithMemberOptions(SymbolDisplayMemberOptions.None)
                .WithKindOptions(SymbolDisplayKindOptions.None)
                .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes)
                .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters);
            return type.ToDisplayString(fmt);
        }
    }
} 