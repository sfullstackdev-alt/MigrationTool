namespace MigrationTool.CloneAndBuild.Services;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MigrationTool.CloneAndBuild.Models;

/// <summary>
/// Inspects a cloned repo and infers SDK/workload/package prerequisites for containerized builds.
/// </summary>
public sealed class AnalyzeRepo
{
    // Fallback image when no repo-specific SDK hint can be found.
    private const string DefaultBuildImage = "mcr.microsoft.com/dotnet/sdk:8.0";

    private static readonly Regex SdkBandRegex = new(
        "^(?<major>\\d+)\\.(?<minor>\\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TfmRegex = new(
        "^net(?<major>\\d+)\\.(?<minor>\\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public Task<RepoAnalysisResult> AnalyzeAsync(
        string repoPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        if (!Directory.Exists(repoPath))
        {
            return Task.FromResult(
                new RepoAnalysisResult(
                    repoPath,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    new[] { $"Repository path does not exist: {repoPath}" },
                    DefaultBuildImage));
        }

        var sdkBands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var additionalPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        // Collect hints from both global.json and project files.
        AnalyzeGlobalJson(repoPath, sdkBands, warnings);
        AnalyzeProjectFiles(repoPath, sdkBands, workloads, additionalPackages, warnings, cancellationToken);

        var orderedSdkBands = OrderSdkBandsDescending(sdkBands).ToList();
        var orderedWorkloads = workloads.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
        var orderedPackages = additionalPackages.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var suggestedImage = orderedSdkBands.Count == 0
            ? DefaultBuildImage
            : $"mcr.microsoft.com/dotnet/sdk:{orderedSdkBands[0]}";

        return Task.FromResult(
            new RepoAnalysisResult(
                repoPath,
                orderedSdkBands,
                orderedWorkloads,
                orderedPackages,
                warnings,
                suggestedImage));
    }

    private static void AnalyzeGlobalJson(
        string repoPath,
        ISet<string> sdkBands,
        IList<string> warnings)
    {
        var globalJsonPath = Path.Combine(repoPath, "global.json");
        if (!File.Exists(globalJsonPath))
        {
            return;
        }

        try
        {
            // global.json is the strongest SDK signal for dotnet CLI behavior.
            var rootNode = JsonNode.Parse(File.ReadAllText(globalJsonPath));
            var version = rootNode?["sdk"]?["version"]?.ToString();
            if (!string.IsNullOrWhiteSpace(version)
                && TryExtractSdkBand(version, out var sdkBand))
            {
                sdkBands.Add(sdkBand);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to parse global.json: {ex.Message}");
        }
    }

    private static void AnalyzeProjectFiles(
        string repoPath,
        ISet<string> sdkBands,
        ISet<string> workloads,
        ISet<string> additionalPackages,
        IList<string> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var projectPath in Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedPath = projectPath.Replace('\\', '/');
            if (normalizedPath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            {
                // Ignore generated artifacts; only source project files should drive analysis.
                continue;
            }

            try
            {
                var projectDocument = XDocument.Load(projectPath);
                AnalyzeTargetFrameworks(projectDocument, sdkBands, workloads, additionalPackages);
                AnalyzePackageReferences(projectDocument, workloads);
                AnalyzeExplicitWorkloadItems(projectDocument, workloads);
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to analyze project file '{projectPath}': {ex.Message}");
            }
        }
    }

    private static void AnalyzeTargetFrameworks(
        XDocument projectDocument,
        ISet<string> sdkBands,
        ISet<string> workloads,
        ISet<string> additionalPackages)
    {
        var tfms = projectDocument.Descendants()
            .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .Select(e => e.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var tfm in tfms)
        {
            // Derive SDK major/minor band from target frameworks.
            if (TryExtractSdkBandFromTfm(tfm, out var sdkBand))
            {
                sdkBands.Add(sdkBand);
            }

            var normalized = tfm.ToLowerInvariant();
            if (normalized.Contains("-android", StringComparison.Ordinal))
            {
                workloads.Add("maui-android");
                // Android workload install needs JDK in Linux container images.
                additionalPackages.Add("openjdk-17-jdk");
            }

            if (normalized.Contains("-ios", StringComparison.Ordinal))
            {
                workloads.Add("maui-ios");
            }

            if (normalized.Contains("-maccatalyst", StringComparison.Ordinal))
            {
                workloads.Add("maui-maccatalyst");
            }

            if (normalized.Contains("-tizen", StringComparison.Ordinal))
            {
                workloads.Add("maui-tizen");
            }

            if (normalized.Contains("-browser", StringComparison.Ordinal)
                || normalized.Contains("-wasm", StringComparison.Ordinal))
            {
                workloads.Add("wasm-tools");
            }
        }
    }

    private static void AnalyzePackageReferences(
        XDocument projectDocument,
        ISet<string> workloads)
    {
        var packages = projectDocument.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v));

        foreach (var package in packages)
        {
            // Aspire package references are treated as an explicit workload requirement.
            if (package!.StartsWith("Aspire.", StringComparison.OrdinalIgnoreCase))
            {
                workloads.Add("aspire");
            }

            if (package.Equals("Microsoft.AspNetCore.Components.WebAssembly", StringComparison.OrdinalIgnoreCase)
                || package.Equals("Microsoft.AspNetCore.Components.WebAssembly.DevServer", StringComparison.OrdinalIgnoreCase))
            {
                workloads.Add("wasm-tools");
            }
        }
    }

    private static void AnalyzeExplicitWorkloadItems(
        XDocument projectDocument,
        ISet<string> workloads)
    {
        var explicitWorkloads = projectDocument.Descendants()
            .Where(e => e.Name.LocalName == "Workload")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var workload in explicitWorkloads)
        {
            workloads.Add(workload);
        }
    }

    private static bool TryExtractSdkBand(string version, out string sdkBand)
    {
        var match = SdkBandRegex.Match(version);
        if (match.Success)
        {
            sdkBand = $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}";
            return true;
        }

        sdkBand = string.Empty;
        return false;
    }

    private static bool TryExtractSdkBandFromTfm(string tfm, out string sdkBand)
    {
        var match = TfmRegex.Match(tfm);
        if (match.Success)
        {
            sdkBand = $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}";
            return true;
        }

        sdkBand = string.Empty;
        return false;
    }

    private static IEnumerable<string> OrderSdkBandsDescending(IEnumerable<string> sdkBands)
    {
        // Prefer the highest detected SDK band for image selection.
        return sdkBands
            .Select(band => (Band: band, Sort: ToSortKey(band)))
            .OrderByDescending(x => x.Sort.Major)
            .ThenByDescending(x => x.Sort.Minor)
            .Select(x => x.Band);
    }

    private static (int Major, int Minor) ToSortKey(string sdkBand)
    {
        if (!TryExtractSdkBand(sdkBand, out var normalized))
        {
            return (0, 0);
        }

        var parts = normalized.Split('.', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor))
        {
            return (0, 0);
        }

        return (major, minor);
    }
}
