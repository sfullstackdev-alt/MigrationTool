namespace MigrationTool.CloneAndBuild.Abstractions;

using MigrationTool.CloneAndBuild.Models;

public interface ICloneRepo
{
    /// <summary>
    /// Clones the repository from the requested branch and creates a new branch named
    /// {clonedAppName}-{shortGuid}.
    /// </summary>
    Task<CloneRepoResult> CloneAsync(
        string dotnetRepoUrl,
        string destinationRoot,
        string dotnetRepoBranch,
        CancellationToken cancellationToken = default);
}
