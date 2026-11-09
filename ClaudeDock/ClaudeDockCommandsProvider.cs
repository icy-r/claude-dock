using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ClaudeDock;

public sealed partial class ClaudeDockCommandsProvider : CommandProvider
{
    public const string ProviderId = "com.claudedock";
    public const string BandId = "com.claudedock.sessions";

    private readonly SessionsBand _band = new();
    private readonly Work.WorkBand _workBand = new();
    private readonly ICommandItem[] _commands;

    public ClaudeDockCommandsProvider()
    {
        Id = ProviderId;
        DisplayName = "Claude Dock";
        Icon = Icons.Claude;
        _commands =
        [
            new CommandItem(new SessionsPage())
            {
                Title = "Claude sessions",
                Subtitle = "Status of running Claude Code sessions",
            },
            new CommandItem(new Work.WorkListPage(Work.WorkKind.Jira)) { Title = "Jira: assigned to me" },
            new CommandItem(new Work.WorkListPage(Work.WorkKind.MyPrs)) { Title = "Bitbucket: my pull requests" },
            new CommandItem(new Work.WorkListPage(Work.WorkKind.Review)) { Title = "Bitbucket: pull requests to review" },
        ];
        Notifier.Init();
        SessionMonitor.Start();
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => [_band, _workBand];
}

internal static class Icons
{
    // Colored SVG dots: orange = needs you, blue = working, green = idle.
    public static readonly IconInfo Claude = new("");     // Robot
    public static readonly IconInfo Waiting = IconHelpers.FromRelativePath("Assets\\status-waiting.svg");
    public static readonly IconInfo Busy = IconHelpers.FromRelativePath("Assets\\status-working.svg");
    public static readonly IconInfo Idle = IconHelpers.FromRelativePath("Assets\\status-idle.svg");
    public static readonly IconInfo None = IconHelpers.FromRelativePath("Assets\\status-unknown.svg");
    public static readonly IconInfo Rename = new("");
    public static readonly IconInfo Go = new("");          // OpenInNewWindow
    public static readonly IconInfo Folder = new("");
    public static readonly IconInfo Copy = new("");

    public static IconInfo For(ClaudeSession s) => s.IsWaiting ? Waiting : s.IsBusy ? Busy : Idle;
}

internal static class Fmt
{
    public static string Ago(DateTime since) => Span(DateTime.Now - since);

    public static string Ago(DateTimeOffset since) => since == default ? "?" : Span(DateTimeOffset.Now - since);

    public static string Span(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)}s"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m"
        : t.TotalHours < 24 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{(int)t.TotalDays}d {t.Hours}h";

    public static string Tokens(long n) => n >= 1000 ? $"{n / 1000}k" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string OneLine(string text, int max)
    {
        var t = string.Join(' ', (text ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Quote text as a Markdown block quote, truncated.</summary>
    public static string Quote(string text, int max)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length > max)
        {
            t = t[..max].TrimEnd() + " …";
        }

        return string.Join('\n', t.Split('\n').Select(l => "> " + l.TrimEnd()));
    }

    public static Tag Tag(string text, byte r, byte g, byte b, string tooltip = "") =>
        new(text) { Foreground = ColorHelpers.FromRgb(255, 255, 255), Background = ColorHelpers.FromRgb(r, g, b), ToolTip = tooltip };

    public static Tag Plain(string text, string tooltip = "") => new(text) { ToolTip = tooltip };
}

internal sealed partial class FocusSessionCommand : InvokableCommand
{
    public FocusSessionCommand(ClaudeSession session)
    {
        Session = session;
        Id = "com.claudedock.focus." + session.Key;
        Name = "Go to terminal";
        Icon = Icons.Go;
    }

    /// <summary>Updated in place on every poll.</summary>
    public ClaudeSession Session { get; set; }

    public override ICommandResult Invoke()
    {
        Log.Write($"Invoke {Session.Key} t={Environment.TickCount64}");

        // Second click within the double-click time: edit the label instead.
        if (DoubleClickDetector.IsDoubleClick(Session.Key))
        {
            RenameDialog.Show(Session);
            return CommandResult.KeepOpen();
        }

        var result = Focuser.Focus(Session);
        SessionMonitor.Kick();
        return result == "OK" ? CommandResult.Dismiss() : CommandResult.ShowToast(result);
    }
}

internal sealed partial class RenameSessionCommand : InvokableCommand
{
    private readonly Func<ClaudeSession> _session;

    public RenameSessionCommand(Func<ClaudeSession> session, string key)
    {
        _session = session;
        Id = "com.claudedock.rename." + key;
        Name = "Rename…";
        Icon = Icons.Rename;
    }

    public override ICommandResult Invoke()
    {
        RenameDialog.Show(_session());
        return CommandResult.KeepOpen();
    }
}

/// <summary>Builds every textual view of a session: dock label, hover text, list row, details pane.</summary>
internal static class SessionView
{
    private const int MaxLabel = 16;

    public static string Short(string text) =>
        text.Length <= MaxLabel ? text : text[..(MaxLabel - 1)].TrimEnd() + "…";

    public static string? Label(ClaudeSession s) => LabelStore.Get(s.SessionId) is { Length: > 0 } l ? l : null;

    /// <summary>The user's label (up to 45 chars, shown in full) or the shortened session title.</summary>
    public static string DockTitle(ClaudeSession s) => Label(s) ?? Short(s.DisplayTitle);

    public static string HostName(ClaudeSession s) => s.Host switch
    {
        TerminalHost.Cursor => "Cursor",
        TerminalHost.WindowsTerminal => "Windows Terminal",
        _ => "unknown terminal",
    };

    public static string Section(ClaudeSession s) => s.IsWaiting ? "Needs you" : s.IsBusy ? "Working" : "Idle";

    public static string StatusLine(ClaudeSession s)
    {
        var dur = Fmt.Ago(s.StatusSince);
        return s.IsWaiting ? $"● NEEDS YOU {dur} — {s.StatusText}"
            : s.IsBusy ? $"● WORKING {dur}"
            : $"● IDLE {dur}";
    }

    public static string? Wants(ClaudeSession s) =>
        s.IsWaiting && s.Transcript.PendingTool.Length > 0
            ? $"{s.Transcript.PendingTool}: {Fmt.OneLine(s.Transcript.PendingToolDetail, 70)}"
            : null;

    /// <summary>Multi-line hover text. The dock shows "Title\nSubtitle" as the tooltip.</summary>
    public static string Hover(ClaudeSession s)
    {
        var t = s.Transcript;
        var lines = new List<string>();
        if (Label(s) is not null || s.DisplayTitle.Length > MaxLabel)
        {
            lines.Add(s.DisplayTitle);
        }

        lines.Add(StatusLine(s));
        if (Wants(s) is { } wants)
        {
            lines.Add("  wants " + wants);
        }

        lines.Add($"{HostName(s)} · {(s.IsWsl ? "WSL " + s.Source : "Windows")}{(t.GitBranch.Length > 0 ? " · ⎇ " + t.GitBranch : string.Empty)}");
        lines.Add(s.Cwd);
        var model = string.Join(" · ", new[] { t.Model, t.PermissionMode, t.ContextTokens > 0 ? Fmt.Tokens(t.ContextTokens) + " ctx" : string.Empty }.Where(x => x.Length > 0));
        if (model.Length > 0)
        {
            lines.Add(model);
        }

        if (t.LastPrompt.Length > 0)
        {
            lines.Add($"Last ask: \"{Fmt.OneLine(t.LastPrompt, 60)}\"");
        }

        if (t.Todos.Count > 0)
        {
            var done = t.Todos.Count(x => x.Status == "completed");
            var current = t.Todos.FirstOrDefault(x => x.Status == "in_progress");
            lines.Add($"Tasks {done}/{t.Todos.Count}{(current is null ? string.Empty : " — now: " + Fmt.OneLine(current.Content, 40))}");
        }

        lines.Add($"Running {Fmt.Ago(DateTimeOffset.FromUnixTimeMilliseconds(s.StartedAt))} · last activity {Fmt.Ago(t.LastActivity)} ago");
        lines.Add("Click: go to terminal · Double-click / right-click: rename");
        return string.Join('\n', lines);
    }

    public static string RowSubtitle(ClaudeSession s) =>
        Wants(s) is { } w ? "wants " + w
        : s.Transcript.LastPrompt.Length > 0 ? "“" + Fmt.OneLine(s.Transcript.LastPrompt, 80) + "”"
        : s.Cwd;

    public static ITag[] Tags(ClaudeSession s)
    {
        var t = s.Transcript;
        var tags = new List<ITag>
        {
            s.IsWaiting ? Fmt.Tag("⏱ " + Fmt.Ago(s.StatusSince), 0xF7, 0x63, 0x0C, "Waiting for you")
            : s.IsBusy ? Fmt.Tag("⏱ " + Fmt.Ago(s.StatusSince), 0x0A, 0x84, 0xFF, "Working")
            : Fmt.Tag("idle " + Fmt.Ago(s.StatusSince), 0x10, 0x9A, 0x09, "Idle"),
            Fmt.Plain(s.Host == TerminalHost.Cursor ? "Cursor" : "Terminal", HostName(s)),
        };
        if (s.IsWsl)
        {
            tags.Add(Fmt.Plain(s.Source, "WSL distro"));
        }

        if (t.GitBranch.Length > 0)
        {
            tags.Add(Fmt.Plain("⎇ " + t.GitBranch, "git branch"));
        }

        if (t.ContextTokens > 0)
        {
            tags.Add(Fmt.Plain(Fmt.Tokens(t.ContextTokens) + " ctx", "context tokens in the last request"));
        }

        return tags.ToArray();
    }

    public static Details Details(ClaudeSession s, ICommand go, ICommand rename)
    {
        var t = s.Transcript;
        var md = new System.Text.StringBuilder();
        md.AppendLine($"**{StatusLine(s).TrimStart('●', ' ')}**");
        if (Wants(s) is not null)
        {
            md.AppendLine();
            md.AppendLine($"**Wants to run** `{t.PendingTool}`");
            md.AppendLine();
            md.AppendLine("```");
            md.AppendLine(t.PendingToolDetail.Length > 600 ? t.PendingToolDetail[..600] + " …" : t.PendingToolDetail);
            md.AppendLine("```");
        }

        if (t.Todos.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("### Tasks");
            foreach (var todo in t.Todos.Take(12))
            {
                var box = todo.Status == "completed" ? "✅" : todo.Status == "in_progress" ? "▶️" : "⬜";
                md.AppendLine($"- {box} {Fmt.OneLine(todo.Content, 90)}");
            }
        }

        if (t.LastPrompt.Length > 0)
        {
            md.AppendLine();
            md.AppendLine("### Last ask");
            md.AppendLine(Fmt.Quote(t.LastPrompt, 500));
        }

        if (t.LastReply.Length > 0)
        {
            md.AppendLine();
            md.AppendLine("### Last reply");
            md.AppendLine(Fmt.Quote(t.LastReply, 900));
        }

        var meta = new List<IDetailsElement>
        {
            new DetailsElement { Key = "Status", Data = new DetailsTags { Tags = Tags(s).Take(1).ToArray() } },
            new DetailsElement { Key = "Where", Data = new DetailsTags { Tags = [Fmt.Plain(HostName(s)), Fmt.Plain(s.IsWsl ? "WSL " + s.Source : "Windows")] } },
            new DetailsElement { Key = "Folder", Data = new DetailsLink { Text = s.Cwd } },
        };
        if (t.GitBranch.Length > 0)
        {
            meta.Add(new DetailsElement { Key = "Branch", Data = new DetailsLink { Text = t.GitBranch } });
        }

        if (t.Model.Length > 0)
        {
            meta.Add(new DetailsElement { Key = "Model", Data = new DetailsLink { Text = $"{t.Model}{(t.PermissionMode.Length > 0 ? " · " + t.PermissionMode : string.Empty)}" } });
        }

        if (t.ContextTokens > 0)
        {
            meta.Add(new DetailsElement { Key = "Context", Data = new DetailsLink { Text = $"{Fmt.Tokens(t.ContextTokens)} tokens · last reply {Fmt.Tokens(t.OutputTokens)} out · {t.ToolCalls} tool calls (recent)" } });
        }

        meta.Add(new DetailsElement { Key = "Running", Data = new DetailsLink { Text = $"{Fmt.Ago(DateTimeOffset.FromUnixTimeMilliseconds(s.StartedAt))} · last activity {Fmt.Ago(t.LastActivity)} ago" } });
        meta.Add(new DetailsElement { Key = "Session", Data = new DetailsLink { Text = $"{s.Name} · {s.SessionId[..Math.Min(8, s.SessionId.Length)]} · pid {s.Pid}{(t.Version.Length > 0 ? " · v" + t.Version : string.Empty)}" } });
        meta.Add(new DetailsElement { Key = string.Empty, Data = new DetailsSeparator() });
        meta.Add(new DetailsElement { Key = "Actions", Data = new DetailsCommands { Commands = [go, rename, new CopyTextCommand(s.Cwd) { Name = "Copy folder path" }] } });

        return new Details
        {
            Title = Label(s) is { } l ? $"{l} — {s.DisplayTitle}" : s.DisplayTitle,
            Body = md.ToString(),
            Metadata = meta.ToArray(),
            Size = ContentSize.Large,
            HeroImage = Icons.For(s),
        };
    }
}

/// <summary>Holds a ListItem per session and updates it in place, so the dock doesn't flicker.</summary>
internal sealed class SessionItemCache
{
    private readonly Dictionary<string, (ListItem Item, FocusSessionCommand Go)> _items = [];
    private readonly bool _forDock;

    public SessionItemCache(bool forDock) => _forDock = forDock;

    public ListItem Get(ClaudeSession s)
    {
        if (!_items.TryGetValue(s.Key, out var entry))
        {
            var go = new FocusSessionCommand(s);
            var item = new ListItem(go);
            var key = s.Key;
            item.MoreCommands = [new CommandContextItem(new RenameSessionCommand(() => go.Session, key))];
            entry = (item, go);
            _items[s.Key] = entry;
        }

        entry.Go.Session = s;
        var i = entry.Item;
        i.Icon = Icons.For(s);
        if (_forDock)
        {
            i.Title = SessionView.DockTitle(s);
            i.Subtitle = SessionView.Hover(s);
        }
        else
        {
            i.Title = SessionView.Label(s) is { } l ? $"{l} — {s.DisplayTitle}" : s.DisplayTitle;
            i.Subtitle = SessionView.RowSubtitle(s);
            i.Section = SessionView.Section(s);
            i.Tags = SessionView.Tags(s);
            i.Details = SessionView.Details(s, entry.Go, new RenameSessionCommand(() => entry.Go.Session, s.Key));
        }

        return i;
    }

    public void Prune(IEnumerable<ClaudeSession> live)
    {
        var keys = live.Select(s => s.Key).ToHashSet();
        foreach (var k in _items.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _items.Remove(k);
        }
    }
}

/// <summary>The dock band: an overview button (opens the flyout) + one button per Claude session.</summary>
internal sealed partial class SessionsBand : WrappedDockItem
{
    private readonly object _gate = new();
    private readonly SessionItemCache _cache = new(forDock: true);
    private readonly ListItem _overview;
    private string _membership = "<init>";

    public SessionsBand()
        : base([], ClaudeDockCommandsProvider.BandId, "Claude sessions")
    {
        Icon = Icons.Claude;
        _overview = new ListItem(new SessionsPage()) { Title = "Claude", Subtitle = "loading…", Icon = Icons.None };
        Items = [_overview];
        SessionMonitor.Changed += OnChanged;
        LabelStore.Changed += () => OnChanged(SessionMonitor.Current);
    }

    private void OnChanged(IReadOnlyList<ClaudeSession> sessions)
    {
        lock (_gate)
        {
            var waiting = sessions.Count(s => s.IsWaiting);
            var busy = sessions.Count(s => s.IsBusy);
            var idle = sessions.Count - waiting - busy;
            _overview.Icon = waiting > 0 ? Icons.Waiting : busy > 0 ? Icons.Busy : sessions.Count > 0 ? Icons.Idle : Icons.None;
            _overview.Title = waiting > 0 ? $"{waiting} need you" : $"Claude {sessions.Count}";
            _overview.Subtitle = sessions.Count == 0
                ? "No Claude sessions"
                : $"{waiting} need you · {busy} working · {idle} idle\n" +
                  string.Join('\n', sessions.Select(s => $"{(s.IsWaiting ? "🟠" : s.IsBusy ? "🔵" : "🟢")} {SessionView.DockTitle(s)} — {s.StatusText} {Fmt.Ago(s.StatusSince)}")) +
                  "\nClick: open the sessions panel";

            var items = sessions.Select(_cache.Get).ToList();
            _cache.Prune(sessions);

            // Replace the item list only when sessions come or go; otherwise items update in place.
            var membership = string.Join("|", sessions.Select(s => s.Key));
            if (membership != _membership)
            {
                _membership = membership;
                Items = [_overview, .. items];
            }
        }
    }
}

/// <summary>Flyout / palette page: sessions grouped by state, with tags and a details pane.</summary>
internal sealed partial class SessionsPage : ListPage
{
    private readonly SessionItemCache _cache = new(forDock: false);
    private readonly object _gate = new();

    public SessionsPage()
    {
        Id = "com.claudedock.sessionsPage";
        Name = "Claude sessions";
        Title = "Claude sessions";
        Icon = Icons.Claude;
        ShowDetails = true;
        PlaceholderText = "Filter sessions…";
        SessionMonitor.Changed += _ => RaiseItemsChanged();
        LabelStore.Changed += () => RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        var sessions = SessionMonitor.Current;
        if (sessions.Count == 0)
        {
            return [new ListItem(new NoOpCommand()) { Title = "No Claude sessions found", Icon = Icons.None }];
        }

        lock (_gate)
        {
            var items = sessions.Select(_cache.Get).Cast<IListItem>().ToArray();
            _cache.Prune(sessions);
            return items;
        }
    }
}
