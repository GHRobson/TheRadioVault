# Alpha9 Conflict Policy Refinement — static validation

Target version: `0.28.0-alpha9-policy-refinement`

Database schema: 44

Library Truth export schema: 6
Registered smoke tests: 98

## Source checks completed

- All 195 C# files were parsed with the C# tree-sitter grammar.
- The only parser error remains the known embedded-JavaScript false positive in `TheRadioVault.Web/Services/LocalWebServer.cs`; all alpha9-modified C# files parse without errors.
- All 37 XAML/project XML files parse successfully.
- Version markers agree across `VERSION.txt`, the desktop project and Library Truth parser.
- The new regression test is registered once and its method exists.
- No database schema or export contract change was introduced.
- No committed or live adoption command was introduced.

## Full-corpus evidence used

The alpha8 schema-6 export contains 15,346 forensic rows: 12,576 auto-resolved and 2,770 unresolved. Field-level analysis confirms the alpha9 rules target generated/structural false positives while leaving the expected 12 genuinely ambiguous/protected rows untouched.

Compilation and execution of all smoke tests still require the user's Windows .NET 8 environment.
