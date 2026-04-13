namespace MigrationTool.CloneAndBuild.Models;

public sealed record BuildIssue(
    string File,
    int? Line,
    int? Column,
    string Code,
    string Message,
    string Severity);
