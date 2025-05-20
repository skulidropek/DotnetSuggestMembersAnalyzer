# Dotnet Suggest Members Analyzer

A Roslyn analyzer for C# that helps developers identify and correct common mistakes when referencing non-existent members, variables, named arguments, or namespaces by providing accurate and intelligent suggestions.

---

## 📦 Installation

You can install the analyzer via NuGet Package Manager:

```sh
dotnet add package SuggestMembersAnalyzer
```

Or via Package Manager Console:

```powershell
Install-Package SuggestMembersAnalyzer
```

---

## 🚀 Features

* **Suggest Members (SMB001)**
  Detects and suggests correct member names when referencing non-existent members.

* **Suggest Variables (SMB002)**
  Suggests similar variable names when referencing undefined variables.

* **Suggest Namespaces (SMB003)**
  Recommends correct namespaces when using incorrect or non-existent namespace references.

* **Suggest Named Arguments (SMB004)**
  Detects misspelled named arguments and provides suggestions with complete signatures for methods and constructors.

* **Suggest Using Nameof (SMB005)**
  Recommends using nameof() operator instead of string literals when referencing code elements, making code more refactoring-friendly.

---

## ⚙️ Usage

Simply reference the analyzer in your project. The analyzer will automatically run in your IDE (Visual Studio, Rider, VS Code, etc.) and display intelligent suggestions as errors or warnings directly in the editor.

Example:

```csharp
var myPanel = new CuiPanel {
    CursorEnalbed = true // Analyzer detects typo and suggests: CursorEnabled
};
```

Diagnostic Message:

```
Member 'CursorEnalbed' does not exist on type 'CuiPanel'. Did you mean:
- CursorEnabled
```

---

## 🚧 Customizing Analyzer Behavior

You can control analyzer severity via ruleset or editorconfig file:

```editorconfig
dotnet_diagnostic.SMB001.severity = error
dotnet_diagnostic.SMB002.severity = error
dotnet_diagnostic.SMB003.severity = error
dotnet_diagnostic.SMB004.severity = error
dotnet_diagnostic.SMB005.severity = error
```

---

## 🔗 Contributing

Feel free to contribute by creating issues or sending pull requests.

Repository: [GitHub/skulidropek/DotnetSuggestMembersAnalyzer](https://github.com/skulidropek/DotnetSuggestMembersAnalyzer)

---

## 📄 License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
