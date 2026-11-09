using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ClaudeDock.Work;

internal static class WorkIcons
{
    public static readonly IconInfo Jira = IconHelpers.FromRelativePath("Assets\\work-jira.svg");
    public static readonly IconInfo JiraAlert = IconHelpers.FromRelativePath("Assets\\work-jira-alert.svg");
    public static readonly IconInfo MyPr = IconHelpers.FromRelativePath("Assets\\work-pr.svg");
    public static readonly IconInfo PrAlert = IconHelpers.FromRelativePath("Assets\\work-pr-alert.svg");
    public static readonly IconInfo PrReady = IconHelpers.FromRelativePath("Assets\\work-pr-ready.svg");
    public static readonly IconInfo Review = IconHelpers.FromRelativePath("Assets\\work-review.svg");
    public static readonly IconInfo ReviewDone = IconHelpers.FromRelativePath("Assets\\work-review-done.svg");
    public static readonly IconInfo Error = IconHelpers.FromRelativePath("Assets\\status-unknown.svg");
    public static readonly IconInfo Refresh = new("");
    public static readonly IconInfo Open = new("");
    public static readonly IconInfo Copy = new("");
}

/// <summary>Dock band: [Jira n] [PRs n · k✓] [Review n]. Hover = summary, click = flyout list with details.</summary>
internal sealed partial class WorkBand : WrappedDockItem
{
    public const string BandId = "com.claudedock.work";

    private readonly ListItem _jira;
    private readonly ListItem _mine;
    private readonly ListItem _review;

    public WorkBand()
        : base([], BandId, "Jira & PRs")
    {
        Icon = WorkIcons.Jira;
        _jira = new ListItem(new WorkListPage(WorkKind.Jira)) { Title = "Jira …", Icon = WorkIcons.Jira };
        _mine = new ListItem(new WorkListPage(WorkKind.MyPrs)) { Title = "PRs …", Icon = WorkIcons.MyPr };
        _review = new ListItem(new WorkListPage(WorkKind.Review)) { Title = "Review …", Icon = WorkIcons.Review };
        Items = [_jira, _mine, _review];
        WorkMonitor.Changed += Update;
        WorkMonitor.Start();
    }

    private void Update(WorkSnapshot s)
    {
        // ---- Jira
        if (s.JiraError is not null)
        {
            (_jira.Title, _jira.Icon, _jira.Subtitle) = ("Jira !", WorkIcons.Error, s.JiraError);
        }
        else
        {
            var urgent = s.Issues.Count(i => i.IsHighPriority || i.IsOverdue || i.IsBlocked);
            _jira.Title = urgent > 0 ? $"Jira {s.Issues.Count} · {urgent}!" : $"Jira {s.Issues.Count}";
            _jira.Icon = urgent > 0 ? WorkIcons.JiraAlert : WorkIcons.Jira;
            _jira.Subtitle = WorkView.JiraHover(s);
        }

        // ---- My PRs + reviews
        if (s.BitbucketError is not null)
        {
            (_mine.Title, _mine.Icon, _mine.Subtitle) = ("PRs !", WorkIcons.Error, s.BitbucketError);
            (_review.Title, _review.Icon, _review.Subtitle) = ("Review !", WorkIcons.Error, s.BitbucketError);
            return;
        }

        var ready = s.MyPrs.Count(p => p.ReadyToMerge);
        var attention = s.MyPrs.Count(p => p.ChangesRequested || p.BuildFailed);
        _mine.Title = $"PRs {s.MyPrs.Count}" + (ready > 0 ? $" · {ready}✓" : string.Empty) + (attention > 0 ? $" · {attention}✗" : string.Empty);
        _mine.Icon = attention > 0 ? WorkIcons.PrAlert : ready > 0 ? WorkIcons.PrReady : WorkIcons.MyPr;
        _mine.Subtitle = WorkView.MyPrsHover(s);

        var pending = s.ToReview.Count(p => !p.IApproved && !p.IsBot);
        var bots = s.ToReview.Count(p => !p.IApproved && p.IsBot);
        _review.Title = $"Review {pending}" + (bots > 0 ? $" +{bots}🤖" : string.Empty);
        _review.Icon = pending > 0 ? WorkIcons.Review : WorkIcons.ReviewDone;
        _review.Subtitle = WorkView.ReviewHover(s);
    }
}

internal enum WorkKind
{
    Jira,
    MyPrs,
    Review,
}

/// <summary>All text/tag/details rendering for Jira issues and pull requests.</summary>
internal static class WorkView
{
    private const int HoverRows = 6;

    // ---------------- hover text ----------------

    public static string JiraHover(WorkSnapshot s)
    {
        var inProgress = s.Issues.Count(i => i.StatusCategory == "indeterminate");
        var todo = s.Issues.Count(i => i.StatusCategory == "new");
        var high = s.Issues.Count(i => i.IsHighPriority);
        var overdue = s.Issues.Count(i => i.IsOverdue);
        var sb = new StringBuilder();
        sb.Append($"{s.Issues.Count} assigned · {inProgress} in progress · {todo} to do");
        if (high + overdue > 0)
        {
            sb.Append($"\n⚠ {high} high priority · {overdue} overdue");
        }

        foreach (var i in s.Issues.Take(HoverRows))
        {
            var flags = (i.IsOverdue ? " ⏰" : string.Empty) + (i.IsHighPriority ? " ▲" : string.Empty);
            sb.Append($"\n{i.Key} [{i.Status}]{flags} {Fmt.OneLine(i.Summary, 55)}");
        }

        if (s.Issues.Count > HoverRows)
        {
            sb.Append($"\n… and {s.Issues.Count - HoverRows} more");
        }

        sb.Append($"\nUpdated {s.UpdatedAt:HH:mm} · click for the full list");
        return sb.ToString();
    }

    public static string MyPrsHover(WorkSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append($"{s.MyPrs.Count} open · {s.MyPrs.Count(p => p.ReadyToMerge)} ready to merge · {s.MyPrs.Count(p => p.ChangesRequested)} changes requested · {s.MyPrs.Count(p => p.BuildFailed)} build failed");
        foreach (var p in s.MyPrs.Take(HoverRows))
        {
            sb.Append($"\n{PrGlyphs(p)} {p.RepoName}#{p.Id} 👤 {p.Author}: {Fmt.OneLine(p.Title, 45)} ({Fmt.Ago(p.Updated)})");
        }

        if (s.MyPrs.Count > HoverRows)
        {
            sb.Append($"\n… and {s.MyPrs.Count - HoverRows} more");
        }

        sb.Append($"\nUpdated {s.UpdatedAt:HH:mm} · click for the full list");
        return sb.ToString();
    }

    public static string ReviewHover(WorkSnapshot s)
    {
        var pending = s.ToReview.Where(p => !p.IApproved && !p.IsBot).ToList();
        var sb = new StringBuilder();
        sb.Append($"{pending.Count} to review · {s.ToReview.Count(p => !p.IApproved && p.IsBot)} bot PRs · {s.ToReview.Count(p => p.IApproved)} approved by you");
        foreach (var p in pending.Take(HoverRows))
        {
            sb.Append($"\n{PrGlyphs(p)} {p.RepoName}#{p.Id} 👤 {p.Author}: {Fmt.OneLine(p.Title, 45)} ({Fmt.Ago(p.Updated)})");
        }

        if (pending.Count > HoverRows)
        {
            sb.Append($"\n… and {pending.Count - HoverRows} more");
        }

        sb.Append($"\nScans {s.ReviewReposScanned} repos (your working set) · click for the full list");
        return sb.ToString();
    }

    private static string PrGlyphs(PullRequest p) =>
        $"✓{p.Approvals}/{Math.Max(p.Reviewers.Length, p.Approvals)}"
        + (p.ChangesRequested ? " ✗" : string.Empty)
        + (p.Build switch { "SUCCESSFUL" => " build ok", "FAILED" => " build FAIL", "STOPPED" => " build stopped", "INPROGRESS" => " build …", _ => string.Empty })
        + (p.Comments > 0 ? $" 💬{p.Comments}" : string.Empty);

    // ---------------- Jira rows ----------------

    public static ListItem JiraItem(JiraIssue i)
    {
        var open = new OpenUrlCommand(i.Url) { Name = "Open in Jira" };
        return new ListItem(open)
        {
            Title = $"{i.Key}  {i.Summary}",
            Subtitle = string.Join(" · ", new[] { i.Type, i.Sprint, i.Parent.Length > 0 ? "↑ " + Fmt.OneLine(i.Parent, 40) : string.Empty }.Where(x => x.Length > 0)),
            Icon = i.IsHighPriority || i.IsOverdue || i.IsBlocked ? WorkIcons.JiraAlert : WorkIcons.Jira,
            Section = i.IsBlocked ? "Blocked" : i.StatusCategory == "indeterminate" ? "In progress" : "To do",
            Tags = JiraTags(i),
            Details = JiraDetails(i, open),
            MoreCommands =
            [
                new CommandContextItem(new CopyTextCommand(i.Key) { Name = "Copy key" }),
                new CommandContextItem(new CopyTextCommand(BranchName(i)) { Name = "Copy branch name" }),
            ],
        };
    }

    private static ITag[] JiraTags(JiraIssue i)
    {
        var tags = new List<ITag>
        {
            i.IsBlocked ? Fmt.Tag(i.Status, 0xE5, 0x48, 0x4D)
            : i.StatusCategory == "indeterminate" ? Fmt.Tag(i.Status, 0x0A, 0x84, 0xFF)
            : Fmt.Tag(i.Status, 0x6E, 0x6E, 0x6E),
        };
        if (i.Priority.Length > 0)
        {
            tags.Add(i.IsHighPriority ? Fmt.Tag("▲ " + i.Priority, 0xE5, 0x48, 0x4D, "priority") : Fmt.Plain(i.Priority, "priority"));
        }

        if (i.Due is { } due)
        {
            tags.Add(i.IsOverdue ? Fmt.Tag($"⏰ due {due:dd MMM}", 0xE5, 0x48, 0x4D) : Fmt.Plain($"due {due:dd MMM}"));
        }

        tags.Add(Fmt.Plain(Fmt.Ago(i.Updated), "last updated"));
        return tags.ToArray();
    }

    private static Details JiraDetails(JiraIssue i, ICommand open)
    {
        var body = new StringBuilder();
        body.AppendLine(i.Description.Length > 0 ? Fmt.Quote(i.Description, 1200) : "_No description._");
        var meta = new List<IDetailsElement>
        {
            Row("Status", new DetailsTags { Tags = JiraTags(i).Take(1).ToArray() }),
            Row("Priority", Text(i.Priority.Length > 0 ? i.Priority : "—")),
            Row("Type", Text(i.Type)),
        };
        if (i.Sprint.Length > 0)
        {
            meta.Add(Row("Sprint", Text(i.Sprint)));
        }

        if (i.Due is { } due)
        {
            meta.Add(Row("Due", Text($"{due:ddd dd MMM yyyy}{(i.IsOverdue ? "  ⏰ overdue" : string.Empty)}")));
        }

        if (i.FixVersions.Length > 0)
        {
            meta.Add(Row("Fix version", Text(string.Join(", ", i.FixVersions))));
        }

        if (i.Labels.Length > 0)
        {
            meta.Add(Row("Labels", new DetailsTags { Tags = i.Labels.Select(l => (ITag)Fmt.Plain(l)).ToArray() }));
        }

        if (i.Parent.Length > 0)
        {
            meta.Add(Row("Parent", Text(i.Parent)));
        }

        meta.Add(Row("Reporter", Text(i.Reporter)));
        meta.Add(Row("Dates", Text($"created {Fmt.Ago(i.Created)} ago · updated {Fmt.Ago(i.Updated)} ago")));
        meta.Add(Row("Link", new DetailsLink(i.Url, i.Url)));
        meta.Add(Row(string.Empty, new DetailsSeparator()));
        meta.Add(Row("Actions", new DetailsCommands
        {
            Commands = [open, new CopyTextCommand(i.Key) { Name = "Copy key" }, new CopyTextCommand(BranchName(i)) { Name = "Copy branch name" }],
        }));
        return new Details { Title = $"{i.Key}  {i.Summary}", Body = body.ToString(), Metadata = meta.ToArray(), Size = ContentSize.Large, HeroImage = WorkIcons.Jira };
    }

    private static string BranchName(JiraIssue i)
    {
        var slug = new string(i.Summary.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        return $"feature/{i.Key}-{(slug.Length > 40 ? slug[..40].TrimEnd('-') : slug)}";
    }

    // ---------------- PR rows ----------------

    public static ListItem PrItem(PullRequest p, bool review)
    {
        var open = new OpenUrlCommand(p.Url) { Name = "Open in Bitbucket" };
        var section = review
            ? (p.IApproved ? "Approved by you" : p.IsBot ? "Bots (Renovate etc.)" : "To review")
            : (p.ChangesRequested || p.BuildFailed ? "Needs attention" : p.ReadyToMerge ? "Ready to merge" : "Waiting for approval");
        var icon = review
            ? (p.IApproved ? WorkIcons.ReviewDone : WorkIcons.Review)
            : (p.ChangesRequested || p.BuildFailed ? WorkIcons.PrAlert : p.ReadyToMerge ? WorkIcons.PrReady : WorkIcons.MyPr);
        return new ListItem(open)
        {
            Title = $"#{p.Id}  {p.Title}",
            Subtitle = $"👤 {p.Author} · {p.RepoName} · {p.SourceBranch} → {p.TargetBranch}",
            Icon = icon,
            Section = section,
            Tags = [Fmt.Tag("👤 " + p.Author, 0x55, 0x4C, 0xA8, "author"), .. PrTags(p)],
            Details = PrDetails(p, open),
            MoreCommands =
            [
                new CommandContextItem(new CopyTextCommand(p.Url) { Name = "Copy link" }),
                new CommandContextItem(new CopyTextCommand(p.SourceBranch) { Name = "Copy source branch" }),
            ],
        };
    }

    private static ITag[] PrTags(PullRequest p)
    {
        var total = Math.Max(p.Reviewers.Length, p.Approvals);
        var tags = new List<ITag>
        {
            p.ReadyToMerge ? Fmt.Tag($"✓ {p.Approvals}/{total}", 0x10, 0x9A, 0x09, "approvals")
            : p.Approvals > 0 ? Fmt.Tag($"✓ {p.Approvals}/{total}", 0x0A, 0x84, 0xFF, "approvals")
            : Fmt.Plain($"✓ 0/{total}", "approvals"),
        };
        if (p.ChangesRequested)
        {
            tags.Add(Fmt.Tag("✗ changes", 0xE5, 0x48, 0x4D, "changes requested"));
        }

        switch (p.Build)
        {
            case "SUCCESSFUL": tags.Add(Fmt.Tag("build ✓", 0x10, 0x9A, 0x09, p.BuildName)); break;
            case "FAILED": tags.Add(Fmt.Tag("build ✗", 0xE5, 0x48, 0x4D, p.BuildName)); break;
            case "STOPPED": tags.Add(Fmt.Tag("build ■ stopped", 0x6E, 0x6E, 0x6E, p.BuildName)); break;
            case "INPROGRESS": tags.Add(Fmt.Tag("build …", 0xC2, 0x8B, 0x00, p.BuildName)); break;
        }

        if (p.Comments > 0)
        {
            tags.Add(Fmt.Plain($"💬 {p.Comments}", "comments"));
        }

        if (p.Tasks > 0)
        {
            tags.Add(Fmt.Plain($"☐ {p.Tasks}", "open tasks"));
        }

        tags.Add(Fmt.Plain(Fmt.Ago(p.Updated), "last updated"));
        return tags.ToArray();
    }

    private static Details PrDetails(PullRequest p, ICommand open)
    {
        var body = p.Description.Length > 0 ? Fmt.Quote(p.Description, 1500) : "_No description._";
        var meta = new List<IDetailsElement>
        {
            Row("Author", new DetailsTags { Tags = [Fmt.Tag("👤 " + p.Author, 0x55, 0x4C, 0xA8, "author")] }),
            Row("Repository", Text(p.Repo)),
            Row("Branches", Text($"{p.SourceBranch}  →  {p.TargetBranch}")),
            Row("Reviewers", new DetailsTags
            {
                Tags = p.Reviewers.Length == 0
                    ? [Fmt.Plain("none")]
                    : p.Reviewers.Select(r => (ITag)(r.State switch
                    {
                        "approved" => Fmt.Tag("✓ " + r.Name, 0x10, 0x9A, 0x09, "approved"),
                        "changes_requested" => Fmt.Tag("✗ " + r.Name, 0xE5, 0x48, 0x4D, "changes requested"),
                        _ => Fmt.Plain(r.Name, "not reviewed yet"),
                    })).ToArray(),
            }),
            Row("Checks", new DetailsTags { Tags = PrTags(p) }),
        };
        // One row per build of the head commit (latest run each), linked to the run.
        foreach (var b in p.Builds)
        {
            var label = $"{b.State switch { "SUCCESSFUL" => "✓ passed", "FAILED" => "✗ failed", "INPROGRESS" => "… running", "STOPPED" => "■ stopped", _ => b.State.ToLowerInvariant() }} · {Fmt.Ago(b.Updated)} ago";
            meta.Add(Row(Fmt.OneLine(b.Name, 28), b.Url.Length > 0 ? new DetailsLink(b.Url, label) : Text(label)));
        }

        if (p.Builds.Count == 0)
        {
            meta.Add(Row("Builds", Text("no builds on the head commit")));
        }

        var commands = new List<ICommand> { open, new CopyTextCommand(p.Url) { Name = "Copy link" }, new CopyTextCommand(p.SourceBranch) { Name = "Copy branch" } };
        if (p.JiraKey.Length > 0)
        {
            var jiraUrl = $"{WorkMonitor.JiraSite}/browse/{p.JiraKey}";
            meta.Add(Row("Jira", new DetailsLink(jiraUrl, p.JiraKey)));
            commands.Add(new OpenUrlCommand(jiraUrl) { Name = "Open " + p.JiraKey });
        }

        meta.Add(Row("Dates", Text($"opened {Fmt.Ago(p.Created)} ago · updated {Fmt.Ago(p.Updated)} ago")));
        meta.Add(Row("Link", new DetailsLink(p.Url, $"{p.RepoName} #{p.Id}")));
        meta.Add(Row(string.Empty, new DetailsSeparator()));
        meta.Add(Row("Actions", new DetailsCommands { Commands = commands.ToArray() }));
        return new Details { Title = $"#{p.Id}  {p.Title}", Body = body, Metadata = meta.ToArray(), Size = ContentSize.Large, HeroImage = WorkIcons.MyPr };
    }

    private static DetailsElement Row(string key, IDetailsData data) => new() { Key = key, Data = data };

    private static DetailsLink Text(string text) => new() { Text = text };
}

internal sealed partial class WorkListPage : ListPage
{
    private static readonly string[] JiraOrder = ["Blocked", "In progress", "To do"];
    private static readonly string[] MineOrder = ["Needs attention", "Ready to merge", "Waiting for approval"];
    private static readonly string[] ReviewOrder = ["To review", "Approved by you", "Bots (Renovate etc.)"];

    private readonly WorkKind _kind;

    public WorkListPage(WorkKind kind)
    {
        _kind = kind;
        Id = "com.claudedock.work." + kind.ToString().ToLowerInvariant();
        ShowDetails = true;
        (Name, Title, Icon, PlaceholderText) = kind switch
        {
            WorkKind.Jira => ("Jira", "Jira: assigned to me", WorkIcons.Jira, "Filter issues…"),
            WorkKind.MyPrs => ("My PRs", "Bitbucket: my open pull requests", WorkIcons.MyPr, "Filter pull requests…"),
            _ => ("Review", "Bitbucket: pull requests to review", WorkIcons.Review, "Filter pull requests…"),
        };
        WorkMonitor.Changed += _ => RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        var s = WorkMonitor.Current;
        var refresh = new ListItem(new RefreshWorkCommand())
        {
            Title = "Refresh",
            Subtitle = $"updated {s.UpdatedAt:HH:mm}" + (_kind == WorkKind.Review ? $" · scans {s.ReviewReposScanned} repos (your working set)" : string.Empty),
            Icon = WorkIcons.Refresh,
            Section = "—",
        };
        var error = _kind == WorkKind.Jira ? s.JiraError : s.BitbucketError;
        if (error is not null)
        {
            return [new ListItem(new NoOpCommand()) { Title = error, Icon = WorkIcons.Error }, refresh];
        }

        var items = _kind switch
        {
            WorkKind.Jira => s.Issues.Select(WorkView.JiraItem).OrderBy(i => Array.IndexOf(JiraOrder, i.Section)).ToList(),
            WorkKind.MyPrs => s.MyPrs.Select(p => WorkView.PrItem(p, review: false)).OrderBy(i => Array.IndexOf(MineOrder, i.Section)).ToList(),
            _ => s.ToReview.Select(p => WorkView.PrItem(p, review: true)).OrderBy(i => Array.IndexOf(ReviewOrder, i.Section)).ToList(),
        };

        if (items.Count == 0)
        {
            items.Add(new ListItem(new NoOpCommand()) { Title = "Nothing here", Icon = WorkIcons.ReviewDone });
        }

        items.Add(refresh);
        return items.Cast<IListItem>().ToArray();
    }
}

internal sealed partial class RefreshWorkCommand : InvokableCommand
{
    public RefreshWorkCommand()
    {
        Id = "com.claudedock.work.refresh";
        Name = "Refresh";
        Icon = WorkIcons.Refresh;
    }

    public override ICommandResult Invoke()
    {
        WorkMonitor.Refresh();
        return CommandResult.KeepOpen();
    }
}
