using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MAS_BT.Services;

public record SubHolonLaunchSpec(string? TreePath, string ConfigPath, string ModuleId, string? AgentId);

public interface ISubHolonLauncher
{
    Task LaunchAsync(SubHolonLaunchSpec spec, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default launcher: starts a new MAS-BT process for each sub-holon using the provided config/tree.
/// </summary>
public class ProcessSubHolonLauncher : ISubHolonLauncher
{
    private readonly ILogger _logger;
    private readonly bool _launchInTerminal;

    public ProcessSubHolonLauncher(ILogger logger, bool launchInTerminal = false)
    {
        _logger = logger;
        _launchInTerminal = launchInTerminal;
    }

    public Task LaunchAsync(SubHolonLaunchSpec spec, CancellationToken cancellationToken = default)
    {
        try
        {
            var configPath = Path.GetFullPath(spec.ConfigPath);
            var treePath = spec.TreePath != null ? Path.GetFullPath(spec.TreePath) : null;

            var projectPath = File.Exists("MAS-BT.csproj")
                ? Path.GetFullPath("MAS-BT.csproj")
                : File.Exists(Path.Combine("MAS-BT", "MAS-BT.csproj"))
                    ? Path.GetFullPath(Path.Combine("MAS-BT", "MAS-BT.csproj"))
                    : null;

            // If this launcher was instructed to open terminals, propagate the flag
            var spawnFlag = _launchInTerminal ? "--spawn-terminal " : string.Empty;

            var args = projectPath != null
                ? treePath != null
                    ? $"run --project \"{projectPath}\" --example module-init-test -- {spawnFlag}\"{configPath}\" \"{treePath}\""
                    : $"run --project \"{projectPath}\" --example module-init-test -- {spawnFlag}\"{configPath}\""
                : treePath != null
                    ? $"run --example module-init-test -- {spawnFlag}\"{configPath}\" \"{treePath}\""
                    : $"run --example module-init-test -- {spawnFlag}\"{configPath}\"";

            var psi = BuildProcessStartInfo(args, spec);

            var proc = Process.Start(psi);
            if (proc != null && !_launchInTerminal)
            {
                proc.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        _logger.LogInformation("[SubHolon:{ModuleId}] {Line}", spec.ModuleId, e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        _logger.LogWarning("[SubHolon:{ModuleId}] {Line}", spec.ModuleId, e.Data);
                };

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }

            _logger.LogInformation("SpawnSubHolons: started sub-holon process for {Tree} with config {Config}", treePath, configPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SpawnSubHolons: failed to launch sub-holon for {TreePath}", spec.TreePath);
        }

        return Task.CompletedTask;
    }

    private ProcessStartInfo BuildProcessStartInfo(string dotnetArgs, SubHolonLaunchSpec spec)
    {
        if (!_launchInTerminal)
        {
            return new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = dotnetArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        var terminal = FindTerminalCommand();
        if (terminal == null)
        {
            _logger.LogWarning("SpawnSubHolons: no terminal emulator found; falling back to background process for {ModuleId}", spec.ModuleId);
            return new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = dotnetArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        var shellCmd = $"dotnet {dotnetArgs}";
        var (terminalExe, terminalArgs) = terminal.Value;

        // Build a shell invocation that first sets the terminal title via an escape sequence
        // and then executes the desired command, leaving the shell open afterwards.
        // Try to derive role from the provided config to build title AgentId_Role
        string? roleFromConfig = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(spec.ConfigPath) && File.Exists(spec.ConfigPath))
            {
                var cfgText = File.ReadAllText(spec.ConfigPath);
                using var d = JsonDocument.Parse(cfgText);
                if (d.RootElement.TryGetProperty("Agent", out var agentElem) && agentElem.ValueKind == JsonValueKind.Object)
                {
                    if (agentElem.TryGetProperty("Role", out var roleElem) && roleElem.ValueKind == JsonValueKind.String)
                    {
                        roleFromConfig = roleElem.GetString();
                    }
                }
            }
        }
        catch
        {
            // ignore parse errors
        }

        var baseAgentId = spec.AgentId ?? spec.ModuleId ?? "subholon";
        var rolePart = !string.IsNullOrWhiteSpace(roleFromConfig) ? roleFromConfig : "unknown";
        var title = $"{baseAgentId}_{rolePart}";
        // sanitize title to remove problematic characters
        var sanitizedTitle = title.Replace('"', '_').Replace('\'', '_').Replace(' ', '_').Replace('/', '_').Replace('\\', '_');
        // Use ANSI sequence to set title: ESC ] 0 ; title BEL
        var bashCmd = $"printf '\\033]0;{sanitizedTitle}\\007'; {shellCmd}; exec bash";

        string argsWithTitle;
        // Most terminals accept '-e bash -c "..."' or '-- bash -c "..."'
        if (terminalExe.Contains("gnome-terminal"))
        {
            argsWithTitle = $"-- bash -c \"{bashCmd}\"";
        }
        else
        {
            // Default to using '-e bash -c "..."' which works for xterm/xfce4-terminal/konsole/x-terminal-emulator
            argsWithTitle = $"-e bash -c \"{bashCmd}\"";
        }

        return new ProcessStartInfo
        {
            FileName = terminalExe,
            Arguments = argsWithTitle,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };
    }

    private static (string exe, string args)? FindTerminalCommand()
    {
        var candidates = new[]
        {
            ("x-terminal-emulator", "-e {0}"),
            ("gnome-terminal", "-- bash -c \"{0}; exec bash\""),
            ("konsole", "-e {0}"),
            ("xfce4-terminal", "-e {0}"),
            ("xterm", "-e {0}")
        };

        foreach (var (exe, args) in candidates)
        {
            if (IsOnPath(exe))
            {
                return (exe, args);
            }
        }

        return null;
    }

    private static bool IsOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var parts = path.Split(':');
        foreach (var p in parts)
        {
            var candidate = Path.Combine(p, command);
            if (File.Exists(candidate)) return true;
        }

        return false;
    }
}
