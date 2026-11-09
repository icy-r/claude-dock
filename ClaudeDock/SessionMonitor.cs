using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeDock;

/// <summary>
/// Polls `claude agents --json` on Windows and in every running WSL distro.
/// </summary>
internal static class SessionMonitor
{
    private const string ListClaudeScript =
        "c=\"$HOME/.local/bin/claude\"; [ -x \"$c\" ] || c=$(command -v claude 2>/dev/null); [ -n \"$c\" ] && exec \"$c\" agents --json";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);
    private static readonly ConcurrentDictionary<string, bool> DistroHasClaude = new();
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static int _polling;

    public static event Action<IReadOnlyList<ClaudeSession>>? Changed;

    /// <summary>Raised when a session goes into a "needs you" state.</summary>
    public static event Action<ClaudeSession>? StartedWaiting;

    private static readonly ConcurrentDictionary<string, (string Status, DateTime Since)> StatusHistory = new();
    private static bool _firstPollDone;

    private static void TrackStatus(ClaudeSession s)
    {
        var now = DateTime.Now;
        if (StatusHistory.TryGetValue(s.Key, out var prev) && prev.Status == s.Status)
        {
            s.StatusSince = prev.Since;
            return;
        }

        // First sighting: the transcript's last activity is the best guess for when this state began.
        var since = prev.Status is null && s.Transcript.LastActivity != default && s.Transcript.LastActivity.LocalDateTime < now
            ? s.Transcript.LastActivity.LocalDateTime
            : now;
        s.StatusSince = since;
        StatusHistory[s.Key] = (s.Status, since);

        // Don't toast for sessions that were already waiting when the dock started.
        if (s.IsWaiting && _firstPollDone && (prev.Status is null || !IsWaitingStatus(prev.Status)))
        {
            StartedWaiting?.Invoke(s);
        }
    }

    private static bool IsWaitingStatus(string status) => status is "waiting" or "blocked";

    public static IReadOnlyList<ClaudeSession> Current { get; private set; } = [];

    public static void Start()
    {
        lock (Gate)
        {
            _timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, Interval);
        }
    }

    /// <summary>Forces an immediate refresh (e.g. after a click).</summary>
    public static void Kick() => ThreadPool.QueueUserWorkItem(_ => Tick());

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            var sessions = PollOnce();
            _firstPollDone = true;
            Current = sessions;
            Changed?.Invoke(sessions);
        }
        catch (Exception ex)
        {
            Log.Write($"Poll failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    public static List<ClaudeSession> PollOnce()
    {
        var tasks = new List<Task<List<ClaudeSession>>>
        {
            Task.Run(PollWindows),
        };

        foreach (var distro in RunningDistros())
        {
            if (DistroHasClaude.TryGetValue(distro, out var has) && !has)
            {
                continue;
            }

            tasks.Add(Task.Run(() => PollWsl(distro)));
        }

        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(20));
        var all = tasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result).ToList();

        foreach (var s in all)
        {
            SessionInfoResolver.Resolve(s);
            TrackStatus(s);
        }

        return all
            .OrderBy(s => s.SortRank)
            .ThenByDescending(s => s.StartedAt)
            .ToList();
    }

    private static List<ClaudeSession> PollWindows()
    {
        var exe = FindWindowsClaude();
        if (exe is null)
        {
            return [];
        }

        var json = ProcessRunner.Run(exe, ["agents", "--json"]);
        return Parse(json, "Windows");
    }

    private static List<ClaudeSession> PollWsl(string distro)
    {
        var json = ProcessRunner.Run(ProcessRunner.WslExe, ["-d", distro, "-e", "sh", "-c", ListClaudeScript]);
        if (string.IsNullOrWhiteSpace(json))
        {
            // No claude in this distro (or it failed). Remember "no claude" only when it answered quickly with nothing.
            DistroHasClaude.TryAdd(distro, json is not null ? false : true);
            return [];
        }

        DistroHasClaude[distro] = true;
        return Parse(json, distro);
    }

    private static string? _windowsClaude;

    private static string? FindWindowsClaude()
    {
        if (_windowsClaude is not null && File.Exists(_windowsClaude))
        {
            return _windowsClaude;
        }

        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
        }

        _windowsClaude = candidates.FirstOrDefault(File.Exists);
        return _windowsClaude;
    }

    public static IEnumerable<string> RunningDistros()
    {
        var output = ProcessRunner.Run(ProcessRunner.WslExe, ["-l", "--running", "-q"], 10000);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().TrimStart('﻿'))
            .Where(l => l.Length > 0 && !l.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<ClaudeSession> Parse(string? json, string source)
    {
        var list = new List<ClaudeSession>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }

        var start = json.IndexOf('[');
        if (start < 0)
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(json[start..]);
            var dayAgo = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var kind = Str(e, "kind");
                var status = kind == "background" ? Str(e, "state") : Str(e, "status");
                var startedAt = e.TryGetProperty("startedAt", out var sa) && sa.TryGetInt64(out var v) ? v : 0;

                // Background jobs: only show live-ish ones from the last day.
                if (kind == "background" && (startedAt < dayAgo || status is "done" or "failed" or "stopped"))
                {
                    continue;
                }

                list.Add(new ClaudeSession
                {
                    Source = source,
                    Pid = e.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var p) ? p : 0,
                    Cwd = Str(e, "cwd"),
                    Kind = kind,
                    SessionId = Str(e, "sessionId"),
                    Name = Str(e, "name"),
                    Status = status,
                    WaitingFor = Str(e, "waitingFor"),
                    StartedAt = startedAt,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Parse {source} failed: {ex.Message}");
        }

        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
