namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>Source languages supported by a formatting check.</summary>
[Flags]
public enum FormatLanguage { Cpp = 1, CMake = 2, All = Cpp | CMake }
