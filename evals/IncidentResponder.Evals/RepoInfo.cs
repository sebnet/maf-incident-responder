using System.Diagnostics;
using System.Reflection;
using Microsoft.Agents.AI;

namespace IncidentResponder.Evals;

/// <summary>Where the repo is and which version of it (and of MAF) a run used.</summary>
public static class RepoInfo
{
    private const string SolutionFile = "IncidentResponder.slnx";

    /// <summary>Walks up from <paramref name="start"/> to the folder holding the solution file.</summary>
    public static string FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFile)))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException($"{SolutionFile} not found above {start}.");
    }

    public static string MafVersion() =>
        typeof(AIAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    /// <summary>Short commit hash, "+dirty" when the working tree has changes, "unknown" without git.</summary>
    public static string Commit(string repoRoot)
    {
        var head = Git(repoRoot, "rev-parse --short HEAD");
        if (head is null)
        {
            return "unknown";
        }

        return string.IsNullOrWhiteSpace(Git(repoRoot, "status --porcelain")) ? head : head + "+dirty";
    }

    private static string? Git(string repoRoot, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git not installed
        }
    }
}
