using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SuggestMembersAnalyzer
{
    // Part of the analyzer responsible for checking local variables, classes, and methods
    public partial class SuggestMembersAnalyzer
    {
        // List of C# keywords that should not be analyzed as variables
        private static readonly HashSet<string> CSharpKeywords =
        [
            "var", "dynamic", "void", "string", "int", "long", "bool", "float", "double", "decimal",
            "byte", "short", "char", "object", "typeof", "sizeof", "null", "true", "false",
            "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break",
            "continue", "return", "yield", "try", "catch", "finally", "throw", "using", "new",
            "class", "struct", "enum", "interface", "delegate", "event", "namespace", "in", "out",
            "ref", "params", "this", "base", "static", "readonly", "const", "virtual", "override",
            "abstract", "sealed", "extern", "public", "private", "protected", "internal", "async", "await", "nameof"
        ];

        private static void AnalyzeIdentifier(SyntaxNodeAnalysisContext context)
        {
            var identifier = (IdentifierNameSyntax)context.Node;
            var identifierName = identifier.Identifier.Text;

            // Skip C# keywords
            if (CSharpKeywords.Contains(identifierName))
            {
                return;
            }

            // Skip if this identifier is part of a member access, declaration, etc.
            if (IsPartOfMemberAccessOrDeclaration(identifier))
            {
                return;
            }

            // Additional check to skip parts of namespace in using directives
            // Check all parent nodes for UsingDirectiveSyntax
            for (SyntaxNode? current = identifier.Parent; current != null; current = current.Parent)
            {
                if (current is UsingDirectiveSyntax)
                {
                    return;
                }
            }

            // If we've made it this far, we have an identifier which could be either:
            // - a local variable or parameter
            // - a type name (class, struct, etc.)
            // - a namespace component
            // - a completely undefined symbol

            var semanticModel = context.SemanticModel;
            var symbol = semanticModel.GetSymbolInfo(identifier).Symbol;

            if (symbol != null)
            {
                return;
            }

            // The identifier doesn't exist, let's try to find suggestions

            if (semanticModel.GetSymbolInfo(identifier).CandidateReason == CandidateReason.OverloadResolutionFailure)
            {
                return;
            }

            // At this point, we're sure the identifier is completely undefined

            // Look for variables similar to the identifier in the current scope

            // Get the local scope for this identifier
            var scope = GetLocalScope(identifier);
            if (scope != null)
            {
                var similarVariablesInScope = GetSimilarVariablesInScope(semanticModel, scope, identifier, identifierName);

                // Get the containing type (class, struct, etc.) to look for its members
                var enclosingSymbol = semanticModel.GetEnclosingSymbol(identifier.SpanStart);
                var containingType = enclosingSymbol?.ContainingType;
                var classMemberCandidates = new List<(string name, string fullName, double similarity)>();

                // Create dictionary to track all methods with their return types
                var allDefinedMethods = new Dictionary<string, ITypeSymbol>();

                // Get all accessible class members
                var allClassMembers = new List<ISymbol>();

                if (containingType != null)
                {
                    // Get all instance and static members (methods, properties, fields) of the containing type
                    var classMembers = containingType.GetMembers()
                        .Where(m => m.Kind is SymbolKind.Method or
                                   SymbolKind.Property or
                                   SymbolKind.Field)
                        .ToList();

                    allClassMembers.AddRange(classMembers);
                }

                // Also look for members in the current class/struct/interface
                // This ensures we find methods defined in the same type as where the code is executing
                var currentClassNode = identifier.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (currentClassNode != null && semanticModel.GetDeclaredSymbol(currentClassNode) is INamedTypeSymbol currentClassSymbol && (containingType == null || !containingType.Equals(currentClassSymbol, SymbolEqualityComparer.Default)))
                {
                    var currentClassMembers = currentClassSymbol.GetMembers()
                        .Where(m => m.Kind is SymbolKind.Method or
                                   SymbolKind.Property or
                                   SymbolKind.Field)
                        .ToList();

                    allClassMembers.AddRange(currentClassMembers);
                }

                // Process all found members for similarity
                foreach (var member in allClassMembers)
                {
                    double similarity = Utils.StringSimilarity.ComputeCompositeScore(identifierName, member.Name);
                    // Exclude only exact matches
                    if (similarity > 0.7 && member.Name != identifierName) // Only add members with reasonable similarity
                    {
                        string formattedName;
                        if (member is IMethodSymbol method)
                        {
                            // For methods with full signature, add them to allDefinedMethods dictionary
                            // with the correct return type to avoid creating duplicates with Object
                            allDefinedMethods[method.Name] = method.ReturnType;

                            // For methods use only the full signature
                            formattedName = GetMethodSignature(method);
                            string methodName = member.Name;
                            classMemberCandidates.Add((methodName, formattedName, similarity + 0.01)); // Slightly increase method priority
                        }
                        else if (member is IPropertySymbol property)
                        {
                            formattedName = GetPropertySignature(property);
                            classMemberCandidates.Add((member.Name, formattedName, similarity));
                        }
                        else if (member is IFieldSymbol field)
                        {
                            formattedName = $"{GetFormattedTypeName(field.Type)} {field.Name}";
                            classMemberCandidates.Add((member.Name, formattedName, similarity));
                        }
                        else
                        {
                            formattedName = member.Name;
                            classMemberCandidates.Add((member.Name, formattedName, similarity));
                        }
                    }
                }

                // Check system types for similar names

                // 2. Find similar system types and classes
                var compilation = context.Compilation;

                // Get common system types like Console, String, Int32, etc.
                var commonTypes = GetCommonSystemTypes(compilation);

                // Add all types from all referenced assemblies and the current compilation
                var allTypes = new List<INamedTypeSymbol>(commonTypes);

                // Get all accessible namespaces
                var namespaces = new HashSet<INamespaceSymbol>(SymbolEqualityComparer.Default)
                {
                    compilation.GlobalNamespace
                };

                // Add namespaces from using directives
                var syntaxTree = identifier.SyntaxTree;
                var root = syntaxTree.GetCompilationUnitRoot();
                foreach (var name in root.Usings.Select(u => u.Name).Where(n => n != null))
                {
                    if (name != null && semanticModel.GetSymbolInfo(name).Symbol is INamespaceSymbol namespaceSymbol)
                    {
                        namespaces.Add(namespaceSymbol);
                    }
                }

                // Add all types from all accessible namespaces
                foreach (var ns in namespaces)
                {
                    AddTypesFromNamespace(ns, allTypes);
                }

                // Create type candidates list
                var systemTypeCandidates = new List<(string name, string formattedName, double similarity)>();

                // Add types to candidates list
                foreach (var type in allTypes)
                {
                    double similarity = Utils.StringSimilarity.ComputeCompositeScore(identifierName, type.Name);
                    if (similarity > 0.7) // Only add types with reasonable similarity
                    {
                        string formattedName = type.ToDisplayString();
                        systemTypeCandidates.Add((type.Name, formattedName, similarity));
                    }
                }

                // Combine all candidates
                var allCandidates = new List<(string name, string formattedName, double similarity)>();

                // Add variables with high similarity first (more specific to context)
                foreach (var (name, type) in similarVariablesInScope)
                {
                    double similarity = Utils.StringSimilarity.ComputeCompositeScore(identifierName, name);
                    allCandidates.Add((name, FormatVariableWithType(name, type), similarity));
                }

                // Add class members
                allCandidates.AddRange(classMemberCandidates);

                // Add system types
                allCandidates.AddRange(systemTypeCandidates);

                // Sort all candidates by similarity, highest first
                allCandidates = [.. allCandidates.OrderByDescending(c => c.similarity)];

                // Create lists for diagnostic message
                var topSuggestions = new List<string>();
                var topFormattedSuggestions = new List<string>();

                // Select top 5 unique candidates
                var addedNames = new HashSet<string>();
                var addedMethodSignatures = new HashSet<string>(); // Track method signatures to avoid duplicates

                // Filter out automatically generated backing fields for properties
                var filteredCandidates = allCandidates
                    .Where(c => !c.name.Contains("k__BackingField"))
                    .ToList();

                // First process methods with signatures
                var methodsWithSignature = filteredCandidates
                    .Where(c => c.formattedName.Contains("(") && c.formattedName.Contains(")"))
                    .OrderByDescending(c => c.similarity)
                    .ToList();

                // Process properties and remove duplicates
                var propertyPatterns = new List<string> { "{ get; }", "{ get; set; }", "{ get; } =", "{ get; set; } =" };

                // Extract all property candidates
                var propertyCandidates = filteredCandidates
                    .Where(c => propertyPatterns.Any(pattern => c.formattedName.Contains(pattern)))
                    .ToList();

                // Group properties by name (ignoring type)
                var propertyGroups = propertyCandidates
                    .GroupBy(c => c.name)
                    .ToDictionary(g => g.Key, g => g.ToList());

                // From each group, select the best representation
                var bestProperties = new List<(string name, string formattedName, double similarity)>();
                foreach (var group in propertyGroups)
                {
                    // Prefer full property definition over just the name
                    var bestProperty = group.Value
                        .OrderByDescending(p => p.formattedName.Contains("{ get; set; }") ? 1 : 0)
                        .ThenByDescending(p => p.similarity)
                        .First();

                    bestProperties.Add(bestProperty);
                }

                // Ensure uniqueness by formattedName
                bestProperties = [.. bestProperties
                    .GroupBy(p => p.formattedName)
                    .Select(g => g.First())];

                // Other candidates (not methods or properties)
                var otherCandidates = filteredCandidates
                    .Where(c =>
                        !c.formattedName.Contains("(") &&
                        !c.formattedName.Contains(")") &&
                        !propertyPatterns.Any(pattern => c.formattedName.Contains(pattern)))
                    .ToList();

                // Now, we need to remove variables that have the same name as properties
                otherCandidates = [.. otherCandidates.Where(c => !propertyGroups.ContainsKey(c.name))];

                // Combine lists, prioritizing methods with signatures first, then properties, then other candidates
                var orderedCandidates = methodsWithSignature
                    .Concat(bestProperties)
                    .Concat(otherCandidates)
                    .ToList();

                // Track already added formatted strings to avoid duplicates
                var addedFormattedStrings = new HashSet<string>();

                foreach (var (name, formattedName, _) in orderedCandidates)
                {
                    if (topFormattedSuggestions.Count >= 5)
                    {
                        break;
                    }

                    // Check if we've already added exactly the same formatted representation
                    if (addedFormattedStrings.Contains(formattedName))
                    {
                        continue;
                    }

                    // Check if this is a method
                    bool isMethod = formattedName.Contains("(") && formattedName.Contains(")");

                    if (isMethod)
                    {
                        // For methods, check the full signature to avoid duplicates
                        if (!addedMethodSignatures.Contains(formattedName))
                        {
                            topSuggestions.Add(name);
                            topFormattedSuggestions.Add(formattedName);
                            addedMethodSignatures.Add(formattedName);
                            addedNames.Add(name); // Also add to general names to avoid conflicts with properties
                            addedFormattedStrings.Add(formattedName);
                        }
                    }
                    else
                    {
                        // For non-methods (variables/properties), check if we already added this name
                        if (!addedNames.Contains(name))
                        {
                            topSuggestions.Add(name);
                            topFormattedSuggestions.Add(formattedName);
                            addedNames.Add(name);
                            addedFormattedStrings.Add(formattedName);
                        }
                    }
                }

                if (topSuggestions.Count > 0)
                {
                    // Format suggestions as a bulleted list
                    var suggestionsText = "\n- " + string.Join("\n- ", topFormattedSuggestions);

                    // Create a diagnostic with suggestions
                    var properties = new Dictionary<string, string?>
                    {
                        { "Suggestions", string.Join("|", topSuggestions) }
                    };

                    // Check if there are any suggestions
                    if (suggestionsText.Length == 0)
                    {
                        return;
                    }

                    // Create diagnostic directly with the correct descriptor
                    var diagnostic = Diagnostic.Create(
                        VariableNotFoundRule,
                        identifier.GetLocation(),
                        properties.ToImmutableDictionary(),
                        identifierName,
                        suggestionsText);

                    context.ReportDiagnostic(diagnostic);
                }
                // Don't display diagnostics if there are no suggestions
            }
            else
            {
                // No scope found - also don't display diagnostics
            }
        }

        private static bool IsPartOfMemberAccessOrDeclaration(IdentifierNameSyntax identifier)
        {
            var parent = identifier.Parent;

            // Check if identifier is directly used within a nameof() expression
            // Look for nameof in any parent node configuration
            SyntaxNode? nameofNode = identifier;
            while (nameofNode != null)
            {
                // Check if part of an invocation to nameof
                if (nameofNode.Parent is InvocationExpressionSyntax parentInvocation &&
                    parentInvocation.Expression is IdentifierNameSyntax parentIdentifier &&
                    parentIdentifier.Identifier.Text == "nameof")
                {
                    return true;
                }

                // Move up to parent
                nameofNode = nameofNode.Parent;
            }

            // Check if the identifier is an argument of a nameof() expression
            // First check if parent is ArgumentSyntax and it's inside a nameof call
            if (parent is ArgumentSyntax argument && 
                argument.Parent is ArgumentListSyntax argumentList &&
                argumentList.Parent is InvocationExpressionSyntax nameofInvocation &&
                nameofInvocation.Expression is IdentifierNameSyntax invocationId &&
                invocationId.Identifier.Text == "nameof")
            {
                return true;
            }

            // Check if the identifier is part of a using directive
            if (parent is QualifiedNameSyntax qualifiedName)
            {
                // Walk up to the root node of the qualified name
                SyntaxNode current = qualifiedName;
                while (current.Parent is QualifiedNameSyntax)
                {
                    current = current.Parent;
                }

                // Check if the root node is inside a using directive
                if (current.Parent is UsingDirectiveSyntax)
                {
                    return true;
                }
            }

            // Check directly for identifiers in using
            if (parent is UsingDirectiveSyntax || parent?.Parent is UsingDirectiveSyntax)
            {
                return true;
            }

            // Also check if this identifier is part of a namespace in a using directive
            for (SyntaxNode? currentNode = identifier; currentNode != null; currentNode = currentNode.Parent)
            {
                if (currentNode is UsingDirectiveSyntax)
                {
                    return true;
                }

                if (currentNode is QualifiedNameSyntax && currentNode.Parent is UsingDirectiveSyntax)
                {
                    return true;
                }
            }

            return
                // Skip if part of member access
                (parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == identifier) ||
                // Skip if part of method invocation
                parent is InvocationExpressionSyntax ||
                // Skip if part of declaration
                parent is VariableDeclaratorSyntax ||
                parent is PropertyDeclarationSyntax ||
                parent is MethodDeclarationSyntax ||
                parent is ParameterSyntax ||
                parent is ClassDeclarationSyntax ||
                parent is InterfaceDeclarationSyntax ||
                parent is EnumDeclarationSyntax ||
                parent is NamespaceDeclarationSyntax ||
                // Skip if part of attribute
                parent is AttributeSyntax ||
                // Skip if part of using statement
                parent is UsingDirectiveSyntax ||
                // Check if the identifier is part of a type declaration (typeof(var) or similar)
                parent is TypeOfExpressionSyntax;
        }

        /// <summary>
        /// Gets similar variables in the current scope
        /// </summary>
        private static List<(string name, ITypeSymbol type)> GetSimilarVariablesInScope(
            SemanticModel semanticModel,
            SyntaxNode scope,
            IdentifierNameSyntax identifier,
            string variableName)
        {
            var result = new List<(string name, ITypeSymbol type)>();
            var allDefinedVariables = new Dictionary<string, ITypeSymbol>();
            var methodTypesInScope = new Dictionary<string, ITypeSymbol>(); // Dictionary for methods
            var addedMethodNames = new HashSet<string>(); // Tracking added method names to avoid duplicates

            // Look for local variables in scope
            foreach (var syntaxTree in semanticModel.Compilation.SyntaxTrees)
            {
                // Only look in the same syntax tree
                if (syntaxTree != scope.SyntaxTree)
                {
                    continue;
                }

                // Find all variables in the containing scope
                var variables = syntaxTree.GetRoot()
                    .DescendantNodes(scope.Span)
                    .OfType<VariableDeclaratorSyntax>()
                    .Select(v => v.Identifier.Text)
                    .ToList();

                // Add parameters if we're in a method
                var parameters = scope.Ancestors()
                    .OfType<MethodDeclarationSyntax>()
                    .SelectMany(m => m.ParameterList.Parameters)
                    .Select(p => p.Identifier.Text)
                    .ToList();

                variables.AddRange(parameters);

                // Find field declarations in containing classes
                var fields = scope.Ancestors()
                    .OfType<ClassDeclarationSyntax>()
                    .SelectMany(c => c.Members.OfType<FieldDeclarationSyntax>())
                    .SelectMany(f => f.Declaration.Variables)
                    .Select(v => v.Identifier.Text)
                    .ToList();

                variables.AddRange(fields);

                // Find property declarations in containing classes
                var properties = scope.Ancestors()
                    .OfType<ClassDeclarationSyntax>()
                    .SelectMany(c => c.Members.OfType<PropertyDeclarationSyntax>())
                    .Select(p => p.Identifier.Text)
                    .ToList();

                variables.AddRange(properties);

                // Find method declarations in containing classes
                var methods = scope.Ancestors()
                    .OfType<ClassDeclarationSyntax>()
                    .SelectMany(c => c.Members.OfType<MethodDeclarationSyntax>())
                    .ToList();

                // Add method names with correct return types
                foreach (var method in methods)
                {
                    if (!string.IsNullOrEmpty(method.Identifier.Text))
                    {
                        if (semanticModel.GetDeclaredSymbol(method) is IMethodSymbol methodSymbol)
                        {
                            // Use actual return type instead of Object
                            methodTypesInScope[method.Identifier.Text] = methodSymbol.ReturnType;
                        }
                        else
                        {
                            // Fallback to Object only if we can't get the actual type
                            methodTypesInScope[method.Identifier.Text] = semanticModel.Compilation.GetSpecialType(SpecialType.System_Object);
                        }
                    }
                }

                // Get the type for each variable
                foreach (var variable in variables)
                {
                    if (string.IsNullOrEmpty(variable))
                    {
                        continue;
                    }

                    // Skip the current variable we're analyzing
                    if (variable == variableName)
                    {
                        continue;
                    }

                    // Use DeclarationFinder to find the variable declaration
                    foreach (var syntaxRef in semanticModel.Compilation.SyntaxTrees.SelectMany(st =>
                        st.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
                        .Where(id => id.Identifier.Text == variable)))
                    {
                        if (syntaxRef.Parent?.IsKind(SyntaxKind.VariableDeclarator) == true ||
                            syntaxRef.Parent?.IsKind(SyntaxKind.Parameter) == true ||
                            syntaxRef.Parent?.IsKind(SyntaxKind.PropertyDeclaration) == true ||
                            syntaxRef.Parent?.IsKind(SyntaxKind.FieldDeclaration) == true)
                        {
                            var symbolInfo = semanticModel.GetSymbolInfo(syntaxRef);
                            if (symbolInfo.Symbol is ILocalSymbol localSymbol)
                            {
                                allDefinedVariables[variable] = localSymbol.Type;
                            }
                            else if (symbolInfo.Symbol is IParameterSymbol paramSymbol)
                            {
                                allDefinedVariables[variable] = paramSymbol.Type;
                            }
                            else if (symbolInfo.Symbol is IFieldSymbol fieldSymbol)
                            {
                                allDefinedVariables[variable] = fieldSymbol.Type;
                            }
                            else if (symbolInfo.Symbol is IPropertySymbol propSymbol)
                            {
                                allDefinedVariables[variable] = propSymbol.Type;
                            }
                        }
                    }
                }
            }

            // Look for symbols in the context using the current position
            var symbols = semanticModel.LookupSymbols(identifier.SpanStart);
            foreach (var symbol in symbols)
            {
                // Skip types and namespaces - we're looking for variables
                if (symbol.Kind is SymbolKind.NamedType or SymbolKind.Namespace)
                {
                    continue;
                }

                // Skip the current variable we're analyzing
                if (symbol.Name == variableName)
                {
                    continue;
                }

                if (symbol is ILocalSymbol localSymbol)
                {
                    allDefinedVariables[symbol.Name] = localSymbol.Type;
                }
                else if (symbol is IParameterSymbol parameterSymbol)
                {
                    allDefinedVariables[symbol.Name] = parameterSymbol.Type;
                }
                else if (symbol is IFieldSymbol fieldSymbol)
                {
                    allDefinedVariables[symbol.Name] = fieldSymbol.Type;
                }
                else if (symbol is IPropertySymbol propertySymbol)
                {
                    allDefinedVariables[symbol.Name] = propertySymbol.Type;
                }
                else if (symbol is IMethodSymbol methodSymbol)
                {
                    // Store the actual return type of the method
                    methodTypesInScope[symbol.Name] = methodSymbol.ReturnType;
                }
            }

            // Check similarity and add if above threshold
            foreach (var entry in allDefinedVariables)
            {
                string name = entry.Key;
                ITypeSymbol type = entry.Value;

                double similarity = Utils.StringSimilarity.ComputeCompositeScore(variableName, name);
                // Use a lower threshold for local variables to favor them over system types
                if (similarity >= 0.3 && name != variableName)
                {
                    result.Add((name, type));
                }
            }

            // Add method names if the misspelled identifier is likely a method
            bool isLikelyMethod = identifier.Parent is InvocationExpressionSyntax ||
                                 (identifier.Parent is ExpressionStatementSyntax expr &&
                                  expr.ToString().Contains(variableName + "("));

            if (isLikelyMethod)
            {
                // Get enclosing symbol for containing type
                var enclosingSymbol = semanticModel.GetEnclosingSymbol(identifier.SpanStart);
                var containingType = enclosingSymbol?.ContainingType;

                // First check if methods exist in containing type with proper signatures
                var classMembers = new HashSet<string>();
                if (containingType != null)
                {
                    foreach (var member in containingType.GetMembers().Where(m => m.Kind == SymbolKind.Method))
                    {
                        classMembers.Add(member.Name);
                    }
                }

                // Check current class if different from containing type
                var currentClassNode = identifier.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (currentClassNode != null && semanticModel.GetDeclaredSymbol(currentClassNode) is INamedTypeSymbol currentClassSymbol &&
                    (containingType == null || !containingType.Equals(currentClassSymbol, SymbolEqualityComparer.Default)))
                {
                    foreach (var member in currentClassSymbol.GetMembers().Where(m => m.Kind == SymbolKind.Method))
                    {
                        classMembers.Add(member.Name);
                    }
                }

                // Process methods from lookup symbols
                foreach (var entry in methodTypesInScope)
                {
                    string methodName = entry.Key;
                    ITypeSymbol returnType = entry.Value;

                    double similarity = Utils.StringSimilarity.ComputeCompositeScore(variableName, methodName);
                    if (similarity >= 0.3 && methodName != variableName)
                    {
                        // Skip if we already have this method in class members (it will be processed with full signature)
                        if (classMembers.Contains(methodName))
                        {
                            continue;
                        }

                        // Skip if we've already added this method name
                        if (addedMethodNames.Contains(methodName))
                        {
                            continue;
                        }

                        result.Add((methodName, returnType));
                        addedMethodNames.Add(methodName);
                    }
                }
            }

            // Sort by similarity score descending
            var sortedResult = result
                .OrderByDescending(r => Utils.StringSimilarity.ComputeCompositeScore(variableName, r.name))
                .Take(5)
                .ToList();

            return sortedResult;
        }

        private static void AnalyzeClassReference(SyntaxNodeAnalysisContext context)
        {
            var qualifiedName = (QualifiedNameSyntax)context.Node;
            var leftPart = qualifiedName.Left;

            // We're only interested in the left part if it's a simple identifier
            if (leftPart is IdentifierNameSyntax leftIdentifier)
            {
                var identifierName = leftIdentifier.Identifier.Text;

                // Check if this identifier exists as a symbol
                var semanticModel3 = context.SemanticModel;
                var symbolInfo3 = semanticModel3.GetSymbolInfo(leftIdentifier);

                // If we found a symbol, it means the class exists
                if (symbolInfo3.Symbol != null)
                {
                    return;
                }

                // Otherwise, look for similar class names

                var compilation3 = context.Compilation;
                var similarClasses = new List<(string name, string fullName, double similarity)>();

                // Check for system types first
                var systemTypes = GetCommonSystemTypes(compilation3);
                foreach (var typeSymbol in systemTypes)
                {
                    double similarity = Utils.StringSimilarity.ComputeCompositeScore(identifierName, typeSymbol.Name);
                    // Exclude only exact matches
                    if (similarity >= 0.6 && typeSymbol.Name != identifierName) // Lower threshold for system types
                    {
                        similarClasses.Add((typeSymbol.Name, typeSymbol.ToDisplayString(), similarity));
                    }
                }

                // Sort by similarity and take top 5
                var topSimilarClasses = similarClasses
                    .OrderByDescending(c => c.similarity)
                    .Take(5)
                    .ToList();

                if (topSimilarClasses.Count > 0)
                {
                    var suggestionsText = "\n- " + string.Join("\n- ", topSimilarClasses.Select(c => c.fullName));
                    var suggestions = string.Join("|", topSimilarClasses.Select(c => c.name));

                    // Create diagnostic properties
                    var properties = new Dictionary<string, string?>
                    {
                        { "Suggestions", suggestions }
                    }.ToImmutableDictionary();

                    // Check if there are any suggestions
                    if (suggestionsText.Length == 0)
                    {
                        return;
                    }

                    // Create and report the diagnostic with class descriptor
                    var diagnostic = Diagnostic.Create(
                        VariableNotFoundRule,
                        leftIdentifier.GetLocation(),
                        properties,
                        identifierName,
                        suggestionsText);

                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
    }
}