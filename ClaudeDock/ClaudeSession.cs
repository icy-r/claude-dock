using System;

namespace ClaudeDock;

public enum TerminalHost
{
    Unknown,
    WindowsTerminal,
    Cursor,
}

public sealed class ClaudeSession
{
    /// <summary>"Windows" or the WSL distro name.</summary>
    public string Source { get; init; } = "Windows";

    public bool IsWsl => !string.Equals(Source, "Windows", StringComparison.Ordinal);

    public int Pid { get; init; }

    public string Cwd { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>busy | waiting | idle (interactive) or working | blocked | done ... (background).</summary>
    public string Status { get; init; } = string.Empty;

    public string WaitingFor { get; init; } = string.Empty;

    public long StartedAt { get; init; }

    // Filled in by SessionInfoResolver.
    public string Title { get; set; } = string.Empty;

    public TranscriptInfo Transcript { get; set; } = TranscriptInfo.Empty;

    /// <summary>When the current Status started (tracked by SessionMonitor).</summary>
    public DateTime StatusSince { get; set; } = DateTime.Now;

    public TerminalHost Host { get; set; }

    /// <summary>The session pid plus its ancestors (same pid namespace as Source).</summary>
    public int[] ProcessChain { get; set; } = [];

    public string Key => $"{Source}|{SessionId}|{Pid}";

    public bool IsWaiting => Status is "waiting" or "blocked";

    public bool IsBusy => Status is "busy" or "working";

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Name : Title;

    public string StatusText => Status switch
    {
        "waiting" => string.IsNullOrEmpty(WaitingFor) ? "waiting" : WaitingFor,
        "blocked" => "needs input",
        "busy" or "working" => "working",
        "idle" => "idle",
        _ => Status,
    };

    public int SortRank => IsWaiting ? 0 : IsBusy ? 1 : 2;
}
