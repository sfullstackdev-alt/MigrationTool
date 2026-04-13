namespace MigrationTool.CloneAndBuild.Models;

/// <summary>
/// Captures everything discovered during repository analysis that can influence Docker build setup.
/// </summary>
public sealed record RepoAnalysisResult(
    // Absolute path to the cloned repository that was analyzed.
    string RepoPath,
    // Ordered SDK bands (highest first), e.g. "10.0", "9.0".
    IReadOnlyList<string> SdkBands,
    // Workloads that should be installed before running dotnet build.
    IReadOnlyList<string> Workloads,
    // Extra OS packages needed by detected workloads.
    IReadOnlyList<string> AdditionalPackages,
    // Non-fatal analysis issues; build may still proceed.
    IReadOnlyList<string> Warnings,
    // Suggested dotnet SDK image derived from detected SDK requirements.
    string SuggestedContainerImage);
