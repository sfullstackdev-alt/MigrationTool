namespace MigrationTool.CloneAndBuild.Internal;

using System.Diagnostics;

internal sealed record ProcessExecutionResult(
    int ExitCode,
    string StdOut,
    string StdErr);

internal static class ProcessRunner
{
    public static async Task<ProcessExecutionResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cancellation.
            }
        });

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ProcessExecutionResult(process.ExitCode, stdout, stderr);
    }
}
