namespace MigrationTool.CloneAndBuild.Models;

public sealed record BuildRepoResult(
    string RepoPath,
    bool Succeeded,
    int ExitCode,
    IReadOnlyList<BuildIssue> Errors,
    string StdOut,
    string StdErr);
