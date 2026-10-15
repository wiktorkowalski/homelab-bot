using System.Diagnostics;
using HomelabBot.Configuration;
using Microsoft.Extensions.Options;

namespace HomelabBot.Services;

public sealed record ClaudeProcessResult
{
    public int ExitCode { get; init; }

    public string Stdout { get; init; } = "";

    public string Stderr { get; init; } = "";
}

// Starts claude-run on the host. Cancel kills the process; the caller enforces the timeout.
public interface IClaudeProcessRunner
{
    Task<ClaudeProcessResult> RunAsync(string remoteCommand, string stdin, CancellationToken ct);
}

internal sealed class SshClaudeProcessRunner : IClaudeProcessRunner
{
    private readonly ClaudeRunnerConfiguration _config;

    // A primary constructor with a base list trips StyleCop 1.1.118 (SA1009).
    public SshClaudeProcessRunner(IOptions<ClaudeRunnerConfiguration> config)
    {
        _config = config.Value;
    }

    // The forced command in authorized_keys ignores the command text except as $SSH_ORIGINAL_COMMAND.
    internal static IReadOnlyList<string> Arguments(ClaudeRunnerConfiguration config, string remoteCommand) =>
    [
        "-i", config.KeyPath,
        "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=yes",
        "-o", $"UserKnownHostsFile={config.KnownHostsPath}",
        $"{config.User}@{config.Host}",
        remoteCommand,
    ];

    public async Task<ClaudeProcessResult> RunAsync(string remoteCommand, string stdin, CancellationToken ct)
    {
        var start = new ProcessStartInfo("ssh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in Arguments(_config, remoteCommand))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("ssh did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            try
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // ssh exited before it read stdin (refused key, bad command); its exit code and stderr say why.
            }

            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ClaudeProcessResult { ExitCode = process.ExitCode, Stdout = await stdout, Stderr = await stderr };
    }
}
