using System;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace SuggestMembersAnalyzer
{
    internal static class Resources
    {
        private static readonly ResourceManager _resourceManager
            = new ResourceManager("SuggestMembersAnalyzer.Resources",
                typeof(Resources).GetTypeInfo().Assembly);

        /// <summary>
        /// Gets the resource manager for this assembly
        /// </summary>
        internal static ResourceManager ResourceManager => _resourceManager;

        /// <summary>
        /// The title for the member not found diagnostic
        /// </summary>
        internal static string MemberNotFoundTitle => GetString("MemberNotFoundTitle");

        /// <summary>
        /// The message format for the member not found diagnostic
        /// </summary>
        internal static string MemberNotFoundMessageFormat => GetString("MemberNotFoundMessageFormat");

        /// <summary>
        /// The description for the member not found diagnostic
        /// </summary>
        internal static string MemberNotFoundDescription => GetString("MemberNotFoundDescription");

        /// <summary>
        /// The title for the variable not found diagnostic
        /// </summary>
        internal static string VariableNotFoundTitle => GetString("VariableNotFoundTitle");

        /// <summary>
        /// The message format for the variable not found diagnostic
        /// </summary>
        internal static string VariableNotFoundMessageFormat => GetString("VariableNotFoundMessageFormat");

        /// <summary>
        /// The description for the variable not found diagnostic
        /// </summary>
        internal static string VariableNotFoundDescription => GetString("VariableNotFoundDescription");

        /// <summary>
        /// The title for the namespace not found diagnostic
        /// </summary>
        internal static string NamespaceNotFoundTitle => GetString("NamespaceNotFoundTitle");

        /// <summary>
        /// The message format for the namespace not found diagnostic
        /// </summary>
        internal static string NamespaceNotFoundMessageFormat => GetString("NamespaceNotFoundMessageFormat");

        /// <summary>
        /// The description for the namespace not found diagnostic
        /// </summary>
        internal static string NamespaceNotFoundDescription => GetString("NamespaceNotFoundDescription");

        /// <summary>
        /// The title for the named argument not found diagnostic
        /// </summary>
        internal static string NamedArgumentNotFoundTitle => GetString("NamedArgumentNotFoundTitle");

        /// <summary>
        /// The message format for the named argument not found diagnostic
        /// </summary>
        internal static string NamedArgumentNotFoundMessageFormat => GetString("NamedArgumentNotFoundMessageFormat");

        /// <summary>
        /// The description for the named argument not found diagnostic
        /// </summary>
        internal static string NamedArgumentNotFoundDescription => GetString("NamedArgumentNotFoundDescription");

        private static string GetString(string resourceName)
        {
            // For Roslyn analyzers, we shouldn't use the CurrentUICulture
            var resourceString = _resourceManager.GetString(resourceName, null);

            return !string.IsNullOrEmpty(resourceString)
                ? resourceString
                : GetDefaultResourceString(resourceName);
        }

        private static string GetDefaultResourceString(string resourceName)
        {
            // Fallback strings if resource loading fails
            switch (resourceName)
            {
                case "MemberNotFoundTitle":
                    return "Member not found";

                case "MemberNotFoundMessageFormat":
                    return "Member '{0}' does not exist on type '{1}'. {2}";

                case "MemberNotFoundDescription":
                    return "This member does not exist on the given type.";

                case "VariableNotFoundTitle":
                    return "Variable not found";

                case "VariableNotFoundMessageFormat":
                    return "Variable '{0}' does not exist in the current scope. {1}";

                case "VariableNotFoundDescription":
                    return "This variable does not exist in the current scope.";

                case "NamespaceNotFoundTitle":
                    return "Namespace not found";

                case "NamespaceNotFoundMessageFormat":
                    return "Namespace '{0}' does not exist, Did you mean: {1}";

                case "NamespaceNotFoundDescription":
                    return "This namespace does not exist.";

                case "NamedArgumentNotFoundTitle":
                    return "Named argument not found";

                case "NamedArgumentNotFoundMessageFormat":
                    return "Parameter '{1}' does not exist for {0} '{2}', Available signatures: {3}";

                case "NamedArgumentNotFoundDescription":
                    return "This named argument does not exist for the method or constructor.";

                default:
                    return resourceName;
            }
        }
    }
}