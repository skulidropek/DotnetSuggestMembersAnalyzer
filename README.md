# SuggestMembersAnalyzer

A Roslyn analyzer that detects references to non-existent members, variables, and namespaces in C# code and suggests similar alternatives.

## Overview

SuggestMembersAnalyzer helps developers catch typos and incorrect references early in the development process by leveraging the Roslyn compiler platform to identify and suggest corrections for non-existent code elements.

## Diagnostics

| ID      | Title                      | Category | Default Severity |
|---------|----------------------------|----------|------------------|
| SMB001  | Member does not exist      | Usage    | Error            |
| SMB002  | Variable does not exist    | Usage    | Error            |
| SMB003  | Namespace does not exist   | Usage    | Error            |

## Analysis

### SMB001: Member does not exist

Reports when code attempts to access a member (property, method, field) that doesn't exist on the given type and suggests similar existing members.

#### Example

##### Non-compliant code

```csharp
// Error: 'count' does not exist on type 'List<int>'
var count = items.count;
```

##### Diagnostic message

```
Member 'count' does not exist in type 'List<int>'. Did you mean:
- Count: int
- Count(): int 
- Contains(object): bool
- ConvertAll<TOutput>(Converter<T, TOutput>): List<TOutput>
- Clear(): void
```

### SMB002: Variable does not exist

Reports when code references a variable that doesn't exist in the current scope and suggests similarly named variables.

#### Example

##### Non-compliant code

```csharp
// Error: 'countr' is not declared in the current scope
Console.WriteLine(countr);
```

##### Diagnostic message

```
Variable 'countr' does not exist in the current scope. Did you mean:
- counter: int
- count: int
- content: string
- Convert: Type
- Console: Type
```

### SMB003: Namespace does not exist

Reports when code references a namespace that doesn't exist and suggests similarly named namespaces.

#### Example

##### Non-compliant code

```csharp
// Error: Namespace 'System.Texto' does not exist
using System.Texto;
```

##### Diagnostic message

```
Namespace 'System.Texto' does not exist. Did you mean:
- System.Text
- System.Text.Json
- System.Text.RegularExpressions
- System.Threading
- System.Threading.Tasks
```

## Technical Implementation

SuggestMembersAnalyzer employs several techniques to provide helpful suggestions:

- **Semantic Model Analysis**: Uses Roslyn's semantic model to analyze code and determine if identifiers exist
- **String Similarity**: Employs Jaro-Winkler algorithm and composite metrics to identify similar names
- **Contextual Analysis**: Examines the current scope for available methods, properties, and types
- **Suggestion Prioritization**: Ranks suggestions based on relevance, considering the type and context of use
- **Detailed Formatting**: Presents suggestions in a user-friendly format with method signatures and types

## Configuration

The analyzer's behavior can be controlled through EditorConfig settings.

## Installation

Install the analyzer as a NuGet package:

```
dotnet add package SuggestMembersAnalyzer
```

After installation, the analyzer will be automatically enabled in your development environment (Visual Studio, Rider, VS Code) and will start suggesting corrections during development.

## Requirements

- .NET Standard 2.0 or higher
- Compatible with Visual Studio 2019+ and other IDEs that support Roslyn analyzers 