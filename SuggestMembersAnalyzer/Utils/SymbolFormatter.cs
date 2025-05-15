using Microsoft.CodeAnalysis;
using System.Linq;

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
    }
} 