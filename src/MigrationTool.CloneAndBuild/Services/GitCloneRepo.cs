namespace MigrationTool.CloneAndBuild.Services;

using System.Text.RegularExpressions;
using MigrationTool.CloneAndBuild.Abstractions;
using MigrationTool.CloneAndBuild.Internal;
using MigrationTool.CloneAndBuild.Models;

public sealed class GitCloneRepo : ICloneRepo
{
    public async Task<CloneRepoResult> CloneAsync(
        string dotnetRepoUrl,
        string destinationRoot,
        string dotnetRepoBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetRepoUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetRepoBranch);

        Directory.CreateDirectory(destinationRoot);

        var clonedAppName = GetAppName(dotnetRepoUrl);
        var localPath = Path.Combine(destinationRoot, clonedAppName);
        var branchName = BuildBranchName(clonedAppName);

        if (Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any())
        {
            return new CloneRepoResult(
                dotnetRepoUrl,
                localPath,
                branchName,
                false,
                -1,
                string.Empty,
                $"Destination path already exists and is not empty: {localPath}");
        }

        var cloneResult = await ProcessRunner.RunAsync(
            "git",
            $"clone --branch {Quote(dotnetRepoBranch)} --single-branch {Quote(dotnetRepoUrl)} {Quote(localPath)}",
            workingDirectory: null,
            cancellationToken);

        if (cloneResult.ExitCode != 0)
        {
            return new CloneRepoResult(
                dotnetRepoUrl,
                localPath,
                branchName,
                false,
                cloneResult.ExitCode,
                cloneResult.StdOut,
                cloneResult.StdErr);
        }

        var branchResult = await ProcessRunner.RunAsync(
            "git",
            $"checkout -b {Quote(branchName)}",
            localPath,
            cancellationToken);

        var success = branchResult.ExitCode == 0;

        return new CloneRepoResult(
            dotnetRepoUrl,
            localPath,
            branchName,
            success,
            branchResult.ExitCode,
            JoinOutput(cloneResult.StdOut, branchResult.StdOut),
            JoinOutput(cloneResult.StdErr, branchResult.StdErr));
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string JoinOutput(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return $"{first}{Environment.NewLine}{second}";
    }

    private static string GetAppName(string repoUrl)
    {
        string nameCandidate;

        if (Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
        {
            nameCandidate = uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
        }
        else
        {
            nameCandidate = repoUrl.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        }

        if (nameCandidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            nameCandidate = nameCandidate[..^4];
        }

        return string.IsNullOrWhiteSpace(nameCandidate) ? "app" : nameCandidate;
    }

    private static string BuildBranchName(string clonedAppName)
    {
        var normalized = Regex.Replace(clonedAppName.ToLowerInvariant(), "[^a-z0-9-]", "-");
        normalized = Regex.Replace(normalized, "-+", "-").Trim('-');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "app";
        }

        var shortGuid = Guid.NewGuid().ToString("N")[..8];
        return $"{normalized}-{shortGuid}";
    }
}
