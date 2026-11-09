using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeDock;

/// <summary>
/// Works out, per session: the terminal host, the process chain, and the tab title
/// (the "aiTitle"/"customTitle" Claude Code writes into the transcript and shows as the terminal title).
/// </summary>
internal static partial class SessionInfoResolver
{
    private sealed class Info
    {
        public TerminalHost Host;
        public int[] Chain = [];
        public string? Home;
        public string? TranscriptPath;
        public string Title = string.Empty;
        public TranscriptInfo Transcript = TranscriptInfo.Empty;
        public (DateTime Write, long Length) Stamp;
        public DateTime LastLookup = DateTime.MinValue;
    }

    private static readonly ConcurrentDictionary<string, Info> Cache = new();

    public static void Resolve(ClaudeSession s)
    {
        var info = Cache.GetOrAdd(s.Key, _ => Build(s));
        s.Host = info.Host;
        s.ProcessChain = info.Chain;

        if (info.TranscriptPath is null && DateTime.UtcNow - info.LastLookup > TimeSpan.FromSeconds(15))
        {
            info.LastLookup = DateTime.UtcNow;
            info.TranscriptPath = FindTranscript(s, info.Home);
        }

        if (info.TranscriptPath is not null)
        {
            try
            {
                var fi = new FileInfo(info.TranscriptPath);
                var stamp = (fi.LastWriteTimeUtc, fi.Length);
                if (stamp != info.Stamp)
                {
                    // Only re-parse when the transcript changed.
                    info.Stamp = stamp;
                    var previous = info.Transcript;
                    var fresh = TranscriptReader.Read(info.TranscriptPath);

                    // Long tool output can push older facts out of the parsed tail: keep the last known values.
                    info.Transcript = fresh with
                    {
                        LastPrompt = fresh.LastPrompt.Length > 0 ? fresh.LastPrompt : previous.LastPrompt,
                        LastReply = fresh.LastReply.Length > 0 ? fresh.LastReply : previous.LastReply,
                        Model = fresh.Model.Length > 0 ? fresh.Model : previous.Model,
                        GitBranch = fresh.GitBranch.Length > 0 ? fresh.GitBranch : previous.GitBranch,
                        Todos = fresh.Todos.Count > 0 ? fresh.Todos : previous.Todos,
                    };
                    var t = info.Transcript.Title;
                    if (string.IsNullOrEmpty(t) && string.IsNullOrEmpty(info.Title))
                    {
                        t = ReadTitle(info.TranscriptPath); // title older than the parsed tail
                    }

                    if (!string.IsNullOrEmpty(t))
                    {
                        info.Title = t;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Transcript stat {info.TranscriptPath}: {ex.Message}");
            }
        }

        s.Title = info.Title;
        s.Transcript = info.Transcript;
    }

    private static Info Build(ClaudeSession s)
    {
        var info = new Info();
        if (s.Pid <= 0)
        {
            return info;
        }

        if (!s.IsWsl)
        {
            var chain = NativeProcess.GetAncestry(s.Pid);
            info.Chain = chain.Select(c => c.Pid).ToArray();
            var names = chain.Select(c => c.Name.ToLowerInvariant()).ToList();
            if (names.Contains("cursor.exe") || names.Contains("code.exe"))
            {
                info.Host = TerminalHost.Cursor;
            }
            else if (names.Contains("windowsterminal.exe"))
            {
                info.Host = TerminalHost.WindowsTerminal;
            }

            return info;
        }

        // WSL: read env + ppid chain from /proc in one call.
        var script =
            "echo HOME=$HOME; " +
            $"tr '\\0' '\\n' < /proc/{s.Pid}/environ 2>/dev/null | grep -E '^(TERM_PROGRAM|WT_SESSION)='; " +
            $"p={s.Pid}; i=0; while [ $i -lt 12 ]; do echo ANC=$p; p=$(awk '/^PPid:/{{print $2}}' /proc/$p/status 2>/dev/null); " +
            "[ -z \"$p\" ] && break; [ \"$p\" -le 1 ] && break; i=$((i+1)); done";
        var output = ProcessRunner.Run(ProcessRunner.WslExe, ["-d", s.Source, "-e", "sh", "-c", script]) ?? string.Empty;

        var chainPids = new List<int>();
        string termProgram = string.Empty, wtSession = string.Empty;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("HOME=", StringComparison.Ordinal))
            {
                info.Home = line[5..];
            }
            else if (line.StartsWith("TERM_PROGRAM=", StringComparison.Ordinal))
            {
                termProgram = line[13..];
            }
            else if (line.StartsWith("WT_SESSION=", StringComparison.Ordinal))
            {
                wtSession = line[11..];
            }
            else if (line.StartsWith("ANC=", StringComparison.Ordinal) && int.TryParse(line[4..], out var pid))
            {
                chainPids.Add(pid);
            }
        }

        info.Chain = chainPids.ToArray();
        info.Host = termProgram == "vscode" ? TerminalHost.Cursor
            : wtSession.Length > 0 ? TerminalHost.WindowsTerminal
            : TerminalHost.Unknown;
        Log.Write($"Resolved {s.Source}/{s.Pid}: host={info.Host} chain=[{string.Join(",", info.Chain)}] home={info.Home}");
        return info;
    }

    private static string? FindTranscript(ClaudeSession s, string? wslHome)
    {
        if (string.IsNullOrEmpty(s.SessionId))
        {
            return null;
        }

        string projects;
        if (s.IsWsl)
        {
            if (string.IsNullOrEmpty(wslHome))
            {
                return null;
            }

            projects = $@"\\wsl.localhost\{s.Source}{wslHome.Replace('/', '\\')}\.claude\projects";
        }
        else
        {
            projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        }

        var file = s.SessionId + ".jsonl";
        try
        {
            var direct = Path.Combine(projects, Slug(s.Cwd), file);
            if (File.Exists(direct))
            {
                return direct;
            }

            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var candidate = Path.Combine(dir, file);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"FindTranscript {projects}: {ex.Message}");
        }

        return null;
    }

    private static string Slug(string cwd) => NonAlnum().Replace(cwd, "-");

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlnum();

    [GeneratedRegex("\"(customTitle|aiTitle)\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\")")]
    private static partial Regex TitleRegex();

    /// <summary>Reads the latest custom/ai title from the end of the transcript.</summary>
    internal static string ReadTitle(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int tail = 1024 * 1024;
            if (fs.Length > tail)
            {
                fs.Seek(-tail, SeekOrigin.End);
            }

            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            string? ai = null, custom = null;
            foreach (Match m in TitleRegex().Matches(text))
            {
                var value = JsonSerializer.Deserialize(m.Groups[2].Value, TitleJsonContext.Default.String);
                if (m.Groups[1].Value == "customTitle")
                {
                    custom = value;
                }
                else
                {
                    ai = value;
                }
            }

            return custom ?? ai ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Write($"ReadTitle {path}: {ex.Message}");
            return string.Empty;
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class TitleJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
