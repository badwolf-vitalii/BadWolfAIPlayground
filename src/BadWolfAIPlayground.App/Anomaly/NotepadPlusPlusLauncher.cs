using System.IO;
using System.Diagnostics;
using Microsoft.Win32;

namespace BadWolfAIPlayground.App.Anomaly;

/// <summary>
/// Opens an explicitly selected local log in Notepad++ at the physical
/// original line, without shell command concatenation or file uploads.
/// </summary>
public static class NotepadPlusPlusLauncher
{
    public static ProcessStartInfo CreateStartInfo(
        string executablePath, string logPath, int line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        if (line < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(line),
                "Log line numbers start at 1.");
        }

        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false
        };
        // ArgumentList handles spaces, quotes and non-ASCII path segments
        // safely; Notepad++ supports -n<number> for jump-to-line.
        start.ArgumentList.Add($"-n{line}");
        start.ArgumentList.Add(logPath);
        return start;
    }

    public static bool IsNotepadPlusPlus(string path) =>
        string.Equals(Path.GetFileName(path), "notepad++.exe",
            StringComparison.OrdinalIgnoreCase);

    public static void ValidateExecutable(string path)
    {
        if (!IsNotepadPlusPlus(path) || !File.Exists(path))
        {
            throw new FileNotFoundException(
                "Select an existing notepad++.exe executable.");
        }
    }

    public static string? DiscoverExecutable()
    {
        // Registry App Paths often covers custom/portable installations.
        foreach (string hive in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" })
        {
            try
            {
                string key = hive +
                    @"\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\notepad++.exe";
                if (Registry.GetValue(key, "", null) is string installed &&
                    IsNotepadPlusPlus(installed) && File.Exists(installed))
                {
                    return installed;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or ArgumentException)
            {
                // Registry access is optional; use standard locations instead.
            }
        }

        var locations = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (string? location in locations)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            string candidate = Path.Combine(location, "Notepad++", "notepad++.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (string location in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(location.Trim('"'), "notepad++.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Ignore an invalid PATH entry.
            }
        }
        return null;
    }

    public static void Open(string executablePath, string logPath, int line)
    {
        ValidateExecutable(executablePath);
        if (!File.Exists(logPath))
        {
            throw new FileNotFoundException(
                "The original log file is no longer available.", logPath);
        }

        ProcessStartInfo start = CreateStartInfo(executablePath, logPath, line);
        if (Process.Start(start) is null)
        {
            throw new InvalidOperationException("Notepad++ could not be started.");
        }
    }
}
