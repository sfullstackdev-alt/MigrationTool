namespace MigrationTool.CloneAndBuild.Services;

using System.Xml.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MigrationTool.CloneAndBuild.Abstractions;
using MigrationTool.CloneAndBuild.Internal;
using MigrationTool.CloneAndBuild.Models;

public sealed class DotnetBuildRepo : IBuildRepo
{
    private static readonly Regex BuildErrorWithFileRegex = new(
        "^(?<file>.+?)(?:\\((?<line>\\d+)(?:,(?<column>\\d+))?\\))?:\\s*(?<severity>error|warning)\\s+(?<code>[^:\\s]+):\\s*(?<message>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BuildErrorNoFileRegex = new(
        "^(?<severity>error|warning)\\s+(?<code>[^:\\s]+):\\s*(?<message>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<BuildRepoResult> BuildAsync(
        string repoPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        if (!Directory.Exists(repoPath))
        {
            return new BuildRepoResult(
                repoPath,
                false,
                -1,
                new[]
                {
                    new BuildIssue(string.Empty, null, null, "PATH_NOT_FOUND", $"Repository path does not exist: {repoPath}", "error")
                },
                string.Empty,
                string.Empty);
        }

        var buildResult = await RunBuildAsync(repoPath, cancellationToken);

        if (buildResult.ExitCode != 0 && IsMissingSdkError(buildResult.StdOut, buildResult.StdErr))
        {
            if (TryPatchGlobalJsonForRollForward(repoPath, out var patchMessage))
            {
                var retryResult = await RunBuildAsync(repoPath, cancellationToken);

                buildResult = new ProcessExecutionResult(
                    retryResult.ExitCode,
                    JoinOutput(buildResult.StdOut, $"{patchMessage}{Environment.NewLine}{retryResult.StdOut}"),
                    JoinOutput(buildResult.StdErr, retryResult.StdErr));
            }
        }

        if (buildResult.ExitCode != 0 && HasMissingWorkloadError(buildResult.StdOut, buildResult.StdErr))
        {
            var nonMobileFallback = await TryBuildNonMobileProjectsAsync(repoPath, cancellationToken);
            if (nonMobileFallback is not null)
            {
                buildResult = new ProcessExecutionResult(
                    nonMobileFallback.ExitCode,
                    JoinOutput(buildResult.StdOut, nonMobileFallback.StdOut),
                    JoinOutput(buildResult.StdErr, nonMobileFallback.StdErr));
            }
        }

        var errors = ParseBuildErrors(buildResult.StdOut, buildResult.StdErr, buildResult.ExitCode);

        return new BuildRepoResult(
            repoPath,
            buildResult.ExitCode == 0,
            buildResult.ExitCode,
            errors,
            buildResult.StdOut,
            buildResult.StdErr);
    }

    private static IReadOnlyList<BuildIssue> ParseBuildErrors(string stdOut, string stdErr, int exitCode)
    {
        var issues = new List<BuildIssue>();

        foreach (var line in EnumerateLines(stdOut).Concat(EnumerateLines(stdErr)))
        {
            var withFileMatch = BuildErrorWithFileRegex.Match(line);
            if (withFileMatch.Success)
            {
                var severity = withFileMatch.Groups["severity"].Value.ToLowerInvariant();
                if (severity != "error")
                {
                    continue;
                }

                issues.Add(
                    new BuildIssue(
                        withFileMatch.Groups["file"].Value.Trim(),
                        ParseNullableInt(withFileMatch.Groups["line"].Value),
                        ParseNullableInt(withFileMatch.Groups["column"].Value),
                        withFileMatch.Groups["code"].Value.Trim(),
                        withFileMatch.Groups["message"].Value.Trim(),
                        severity));

                continue;
            }

            var noFileMatch = BuildErrorNoFileRegex.Match(line.TrimStart());
            if (!noFileMatch.Success)
            {
                continue;
            }

            var noFileSeverity = noFileMatch.Groups["severity"].Value.ToLowerInvariant();
            if (noFileSeverity != "error")
            {
                continue;
            }

            issues.Add(
                new BuildIssue(
                    string.Empty,
                    null,
                    null,
                    noFileMatch.Groups["code"].Value.Trim(),
                    noFileMatch.Groups["message"].Value.Trim(),
                    noFileSeverity));
        }

        if (issues.Count == 0 && exitCode != 0)
        {
            var fallbackMessage = EnumerateLines(stdErr)
                .Concat(EnumerateLines(stdOut))
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
                ?.Trim()
                ?? "dotnet build failed with a non-zero exit code.";

            issues.Add(new BuildIssue(string.Empty, null, null, "BUILD_FAILED", fallbackMessage, "error"));
        }

        return issues;
    }

    private static IEnumerable<string> EnumerateLines(string value)
    {
        return value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
    }

    private static bool IsMissingSdkError(string stdOut, string stdErr)
    {
        var output = $"{stdOut}{Environment.NewLine}{stdErr}";
        return output.Contains("A compatible .NET SDK was not found.", StringComparison.OrdinalIgnoreCase)
            || output.Contains("global.json file:", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProcessExecutionResult> RunBuildAsync(string repoPath, CancellationToken cancellationToken)
    {
        if (!IsContainerizedBuildEnabled())
        {
            return await ProcessRunner.RunAsync(
                "dotnet",
                "build --nologo",
                repoPath,
                cancellationToken);
        }

        var buildImage = Environment.GetEnvironmentVariable("DOTNET_BUILD_CONTAINER_IMAGE")
            ?? "mcr.microsoft.com/dotnet/sdk:8.0";
        var buildVolume = Environment.GetEnvironmentVariable("DOTNET_BUILD_CONTAINER_VOLUME");

        if (string.IsNullOrWhiteSpace(buildVolume))
        {
            return new ProcessExecutionResult(
                1,
                string.Empty,
                "DOTNET_BUILD_CONTAINER_VOLUME is required when DOTNET_BUILD_USE_CONTAINER is true.");
        }

        Console.WriteLine($"Building in container image {buildImage} using volume {buildVolume}.");

        return await ProcessRunner.RunAsync(
            "docker",
            $"run --rm --mount type=volume,source={Quote(buildVolume)},target=/repos -w {Quote(repoPath)} {Quote(buildImage)} dotnet build --nologo",
            workingDirectory: null,
            cancellationToken);
    }

    private static async Task<ProcessExecutionResult> RunProjectBuildAsync(
        string repoPath,
        string projectPath,
        CancellationToken cancellationToken)
    {
        if (!IsContainerizedBuildEnabled())
        {
            return await ProcessRunner.RunAsync(
                "dotnet",
                $"build --nologo {Quote(projectPath)}",
                repoPath,
                cancellationToken);
        }

        var buildImage = Environment.GetEnvironmentVariable("DOTNET_BUILD_CONTAINER_IMAGE")
            ?? "mcr.microsoft.com/dotnet/sdk:8.0";
        var buildVolume = Environment.GetEnvironmentVariable("DOTNET_BUILD_CONTAINER_VOLUME");

        if (string.IsNullOrWhiteSpace(buildVolume))
        {
            return new ProcessExecutionResult(
                1,
                string.Empty,
                "DOTNET_BUILD_CONTAINER_VOLUME is required when DOTNET_BUILD_USE_CONTAINER is true.");
        }

        return await ProcessRunner.RunAsync(
            "docker",
            $"run --rm --mount type=volume,source={Quote(buildVolume)},target=/repos -w {Quote(repoPath)} {Quote(buildImage)} dotnet build --nologo {Quote(projectPath)}",
            workingDirectory: null,
            cancellationToken);
    }

    private static bool IsContainerizedBuildEnabled()
    {
        var value = Environment.GetEnvironmentVariable("DOTNET_BUILD_USE_CONTAINER");
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMissingWorkloadError(string stdOut, string stdErr)
    {
        var output = $"{stdOut}{Environment.NewLine}{stdErr}";
        return output.Contains("error NETSDK1147", StringComparison.OrdinalIgnoreCase)
            || output.Contains("workloads must be installed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryPatchGlobalJsonForRollForward(string repoPath, out string patchMessage)
    {
        patchMessage = string.Empty;
        var globalJsonPath = Path.Combine(repoPath, "global.json");
        if (!File.Exists(globalJsonPath))
        {
            return false;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(File.ReadAllText(globalJsonPath));
        }
        catch
        {
            return false;
        }

        if (rootNode is not JsonObject rootObject)
        {
            return false;
        }

        if (rootObject["sdk"] is not JsonObject sdkObject)
        {
            return false;
        }

        var updated = false;
        if (sdkObject["version"] is JsonValue versionValue)
        {
            var rawVersion = versionValue.ToString();
            var normalizedVersion = rawVersion.Split('-', 2)[0];
            if (!string.Equals(rawVersion, normalizedVersion, StringComparison.Ordinal))
            {
                sdkObject["version"] = normalizedVersion;
                updated = true;
            }
        }

        if (!string.Equals(sdkObject["rollForward"]?.ToString(), "latestMajor", StringComparison.OrdinalIgnoreCase))
        {
            sdkObject["rollForward"] = "latestMajor";
            updated = true;
        }

        if (!updated)
        {
            return false;
        }


        var jsonContent = rootObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Writing to {globalJsonPath}:");
        Console.WriteLine(jsonContent);
        File.WriteAllText(globalJsonPath, jsonContent);
        Console.WriteLine($"Patched {globalJsonPath} to enable SDK roll-forward and retry build.");

        patchMessage = $"Patched {globalJsonPath} to enable SDK roll-forward and retry build.";
        return true;
    }

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

    private static async Task<ProcessExecutionResult?> TryBuildNonMobileProjectsAsync(
        string repoPath,
        CancellationToken cancellationToken)
    {
        var candidates = GetNonMobileProjects(repoPath).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var allStdOut = new List<string>
        {
            $"Workload fallback activated. Building {candidates.Count} non-mobile project(s)."
        };
        var allStdErr = new List<string>();
        var hasFailure = false;

        foreach (var projectPath in candidates)
        {
            var result = await RunProjectBuildAsync(repoPath, projectPath, cancellationToken);

            allStdOut.Add($"--- dotnet build {projectPath} (exit {result.ExitCode}) ---");
            if (!string.IsNullOrWhiteSpace(result.StdOut))
            {
                allStdOut.Add(result.StdOut);
            }

            if (!string.IsNullOrWhiteSpace(result.StdErr))
            {
                allStdErr.Add($"--- dotnet build {projectPath} stderr ---");
                allStdErr.Add(result.StdErr);
            }

            if (result.ExitCode != 0)
            {
                hasFailure = true;
            }
        }

        return new ProcessExecutionResult(
            hasFailure ? 1 : 0,
            string.Join(Environment.NewLine, allStdOut),
            string.Join(Environment.NewLine, allStdErr));
    }

    private static IEnumerable<string> GetNonMobileProjects(string repoPath)
    {
        foreach (var projectPath in Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories))
        {
            var normalized = projectPath.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TargetsOnlyMobileFrameworks(projectPath))
            {
                yield return projectPath;
            }
        }
    }

    private static bool TargetsOnlyMobileFrameworks(string projectPath)
    {
        try
        {
            var doc = XDocument.Load(projectPath);
            var tfmValues = doc.Descendants()
                .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .Select(e => e.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();

            if (tfmValues.Count == 0)
            {
                return false;
            }

            return tfmValues.All(IsMobileOrWasmTfm);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMobileOrWasmTfm(string tfm)
    {
        var value = tfm.ToLowerInvariant();
        return value.Contains("-android", StringComparison.Ordinal)
            || value.Contains("-ios", StringComparison.Ordinal)
            || value.Contains("-maccatalyst", StringComparison.Ordinal)
            || value.Contains("-tizen", StringComparison.Ordinal)
            || value.Contains("-browser", StringComparison.Ordinal);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static int? ParseNullableInt(string value)
    {
        return int.TryParse(value, out var number) ? number : null;
    }
}
