namespace MigrationTool.CloneAndBuild.Models;

public sealed record CloneRepoResult(
    string RepositoryUrl,
    string LocalPath,
    string BranchName,
    bool Success,
    int ExitCode,
    string StdOut,
    string StdErr);
