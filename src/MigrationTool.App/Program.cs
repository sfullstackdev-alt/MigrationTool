using MigrationTool.CloneAndBuild.Services;

LoadDotEnvIfPresent();

var dotnetRepoUrl = GetOption(args, "--repo-url")
    ?? Environment.GetEnvironmentVariable("DOTNET_REPO_URL");
var dotnetRepoBranch = GetOption(args, "--repo-branch")
    ?? Environment.GetEnvironmentVariable("DOTNET_REPO_BRANCH")
    ?? "main";
var cloneRoot = GetOption(args, "--clone-root")
    ?? Environment.GetEnvironmentVariable("DOTNET_REPO_CLONE_ROOT")
    ?? "/tmp/cloned-repos";

if (string.IsNullOrWhiteSpace(dotnetRepoUrl))
{
    Console.Error.WriteLine("DOTNET_REPO_URL is required. Set it in environment or pass --repo-url.");
    Environment.Exit(1);
    return;
}

var cloneService = new GitCloneRepo();
var buildService = new DotnetBuildRepo();
try
{
    var cloneResult = await cloneService.CloneAsync(
        dotnetRepoUrl,
        cloneRoot,
        dotnetRepoBranch,
        CancellationToken.None);

    Console.WriteLine($"Repository URL: {cloneResult.RepositoryUrl}");
    Console.WriteLine($"Clone path: {cloneResult.LocalPath}");
    Console.WriteLine($"New branch: {cloneResult.BranchName}");

    if (!cloneResult.Success)
    {
        Console.Error.WriteLine("Clone/branch creation failed.");
        WriteLogs(cloneResult.StdOut, cloneResult.StdErr);
        Environment.Exit(cloneResult.ExitCode == 0 ? 1 : cloneResult.ExitCode);
        return;
    }

    var buildResult = await buildService.BuildAsync(cloneResult.LocalPath, CancellationToken.None);
    Console.WriteLine($"Build exit code: {buildResult.ExitCode}");

    if (!buildResult.Succeeded)
    {
        Console.Error.WriteLine("Build failed.");
        foreach (var issue in buildResult.Errors)
        {
            var lineCol = issue.Line is null ? string.Empty : $":{issue.Line}{(issue.Column is null ? string.Empty : $":{issue.Column}")}";
            var location = string.IsNullOrWhiteSpace(issue.File) ? string.Empty : $"{issue.File}{lineCol} ";
            Console.Error.WriteLine($"{location}{issue.Severity} {issue.Code}: {issue.Message}");
        }

        WriteLogs(buildResult.StdOut, buildResult.StdErr);
        Environment.Exit(buildResult.ExitCode == 0 ? 1 : buildResult.ExitCode);
        return;
    }

    Console.WriteLine("Clone and build completed successfully.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Clone/build pipeline failed: {ex.Message}");
    Environment.Exit(1);
}

static string? GetOption(string[] inputArgs, string optionName)
{
    for (var i = 0; i < inputArgs.Length - 1; i++)
    {
        if (inputArgs[i].Equals(optionName, StringComparison.OrdinalIgnoreCase))
        {
            return inputArgs[i + 1];
        }
    }

    return null;
}

static void LoadDotEnvIfPresent()
{
    var envPath = Path.Combine(AppContext.BaseDirectory, ".env");
    if (!File.Exists(envPath))
    {
        envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
    }

    if (!File.Exists(envPath))
    {
        return;
    }

    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(key) || Environment.GetEnvironmentVariable(key) is not null)
        {
            continue;
        }

        Environment.SetEnvironmentVariable(key, value);
    }
}

static void WriteLogs(string stdOut, string stdErr)
{
    if (!string.IsNullOrWhiteSpace(stdOut))
    {
        Console.WriteLine(stdOut);
    }

    if (!string.IsNullOrWhiteSpace(stdErr))
    {
        Console.Error.WriteLine(stdErr);
    }
}
