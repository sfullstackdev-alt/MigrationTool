namespace MigrationTool.CloneAndBuild.Abstractions;

using MigrationTool.CloneAndBuild.Models;

public interface IBuildRepo
{
    Task<BuildRepoResult> BuildAsync(
        string repoPath,
        CancellationToken cancellationToken = default);
}
