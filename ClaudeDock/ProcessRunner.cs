using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClaudeDock;

internal static class ProcessRunner
{
    public static readonly string WslExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    /// <summary>Runs a process hidden and returns stdout, or null on failure/timeout.</summary>
    public static string? Run(string file, IEnumerable<string> args, int timeoutMs = 15000, Encoding? encoding = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = encoding ?? Encoding.UTF8,
            };
            psi.Environment["WSL_UTF8"] = "1";
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try
                {
                    p.Kill(true);
                }
                catch
                {
                }

                return null;
            }

            return stdout.Result.Replace("\0", string.Empty);
        }
        catch (Exception ex)
        {
            Log.Write($"Run {file} failed: {ex.Message}");
            return null;
        }
    }
}

internal static class Log
{
    private static readonly string LogPath = Path.Combine(DataDir.Path, "claudedock.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    fi.Delete();
                }

                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}

internal static class DataDir
{
    public static readonly string Path = Init();

    private static string Init()
    {
        var p = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-dock");
        Directory.CreateDirectory(p);
        return p;
    }
}
