using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeDock.Work;

internal sealed record JiraIssue
{
    public string Key { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusCategory { get; init; } = string.Empty; // new | indeterminate | done
    public string Priority { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public DateTimeOffset Updated { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTime? Due { get; init; }
    public string Reporter { get; init; } = string.Empty;
    public string[] Labels { get; init; } = [];
    public string[] FixVersions { get; init; } = [];
    public string Sprint { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Parent { get; init; } = string.Empty;

    public bool IsHighPriority => Priority is "Highest" or "High" or "Critical" or "Blocker";
    public bool IsOverdue => Due is { } d && d.Date < DateTime.Today;
    public bool IsBlocked => Status.Contains("block", StringComparison.OrdinalIgnoreCase) || Labels.Any(l => l.Contains("block", StringComparison.OrdinalIgnoreCase));
}

internal sealed record Reviewer(string Name, string Uuid, string State); // State: approved | changes_requested | ""

internal sealed record BuildRun(string Name, string State, string Url, DateTimeOffset Updated);

internal sealed record PullRequest
{
    public string Repo { get; init; } = string.Empty;
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string AuthorUuid { get; init; } = string.Empty;
    public string SourceBranch { get; init; } = string.Empty;
    public string TargetBranch { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }
    public int Comments { get; init; }
    public int Tasks { get; init; }
    public Reviewer[] Reviewers { get; init; } = [];
    public bool IApproved { get; init; }
    public string HeadCommit { get; init; } = string.Empty;
    public string Build { get; set; } = string.Empty; // SUCCESSFUL | FAILED | INPROGRESS | STOPPED | ""
    public string BuildName { get; set; } = string.Empty;
    public string BuildUrl { get; set; } = string.Empty;
    public List<BuildRun> Builds { get; set; } = [];

    public string RepoName => Repo.Split('/').Last();
    public int Approvals => Reviewers.Count(r => r.State == "approved");
    public bool ChangesRequested => Reviewers.Any(r => r.State == "changes_requested");
    public bool BuildFailed => Build == "FAILED";
    public bool IsBot => Author.Contains("renovate", StringComparison.OrdinalIgnoreCase) || Author.Contains("bot", StringComparison.OrdinalIgnoreCase)
        || Title.StartsWith("Renovate", StringComparison.OrdinalIgnoreCase) || SourceBranch.StartsWith("renovate/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Heuristic: approved by every reviewer (or 2+), nothing blocking.</summary>
    public bool ReadyToMerge => !ChangesRequested && !BuildFailed && Reviewers.Length > 0 && (Approvals >= 2 || Approvals == Reviewers.Length);

    public string JiraKey
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(Title + " " + SourceBranch, "\\b([A-Z][A-Z0-9]+-\\d+)\\b");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }
    }
}

internal sealed class WorkSnapshot
{
    public List<JiraIssue> Issues { get; init; } = [];
    public List<PullRequest> MyPrs { get; init; } = [];
    public List<PullRequest> ToReview { get; init; } = [];
    public string? JiraError { get; init; }
    public string? BitbucketError { get; init; }
    public int ReviewReposScanned { get; init; }
    public DateTime UpdatedAt { get; init; } = DateTime.Now;
}

/// <summary>Polls Jira (assigned, not done) and Bitbucket (my open PRs, PRs I review).</summary>
internal static class WorkMonitor
{
    public static string JiraSite => Config.JiraSite;
    private const string Jql = "assignee = currentUser() AND statusCategory != Done ORDER BY priority DESC, updated DESC";
    private const string JiraFields = "summary,status,priority,issuetype,updated,created,duedate,labels,fixVersions,reporter,description,parent,customfield_10020";
    private const string BitbucketApi = "https://api.bitbucket.org/2.0";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly TimeSpan FastInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReviewInterval = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (string Head, List<BuildRun> Builds)> BuildCache = new();
    private static Timer? _timer;
    private static int _polling;
    private static DateTime _lastReviewScan = DateTime.MinValue;
    private static List<PullRequest> _review = [];
    private static int _reviewRepos;
    private static HashSet<string>? _seenReviews;
    private static string? _bbUuid;
    private static string? _jiraBase;
    private static string _jiraBaseSite = string.Empty;
    private static (string Site, string CloudId)? _cloudId;
    private static List<string>? _workspaces;

    public static event Action<WorkSnapshot>? Changed;

    public static event Action<PullRequest>? NewReview;

    public static WorkSnapshot Current { get; private set; } = new() { JiraError = "loading…", BitbucketError = "loading…" };

    public static void Start()
    {
        _timer ??= new Timer(_ => _ = TickAsync(false), null, TimeSpan.FromSeconds(2), FastInterval);
    }

    public static void Refresh() => _ = TickAsync(true);

    private static async Task TickAsync(bool force)
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            var jiraTask = PollJiraAsync();
            var bbTask = PollBitbucketAsync(force);
            await Task.WhenAll(jiraTask, bbTask);
            var (issues, jiraError) = jiraTask.Result;
            var (mine, review, bbError) = bbTask.Result;
            Current = new WorkSnapshot { Issues = issues, MyPrs = mine, ToReview = review, JiraError = jiraError, BitbucketError = bbError, ReviewReposScanned = _reviewRepos };
            Changed?.Invoke(Current);
        }
        catch (Exception ex)
        {
            Log.Write($"Work poll: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    // ---------------- Jira ----------------

    private static async Task<(List<JiraIssue>, string?)> PollJiraAsync()
    {
        var site = JiraSite;
        var cred = Secrets.Jira();
        if (site.Length == 0 || cred is null)
        {
            return ([], "Jira not set up — run set-jira-token.ps1");
        }

        if (_jiraBaseSite != site)
        {
            _jiraBase = null; // site changed in config.json
            _jiraBaseSite = site;
        }

        // Classic tokens work on the site URL; scoped tokens only through the API gateway.
        var bases = new List<string>();
        if (_jiraBase is not null)
        {
            bases.Add(_jiraBase);
        }
        else
        {
            bases.Add(site);
            var cloudId = await CloudIdAsync(site);
            if (cloudId.Length > 0)
            {
                bases.Add($"https://api.atlassian.com/ex/jira/{cloudId}");
            }
        }

        string? lastError = null;
        foreach (var b in bases)
        {
            var url = $"{b}/rest/api/3/search/jql?jql={Uri.EscapeDataString(Jql)}&maxResults=50&fields={JiraFields}";
            var (status, body) = await GetAsync(url, cred);
            if (status == HttpStatusCode.OK && body is not null)
            {
                _jiraBase = b;
                return (ParseIssues(body), null);
            }

            lastError = $"Jira {(int)status}";
        }

        _jiraBase = null;
        return ([], lastError + " — token needs Jira read scope");
    }

    /// <summary>The site's cloud id, from its public tenant_info endpoint (needed for the API gateway).</summary>
    private static async Task<string> CloudIdAsync(string site)
    {
        if (_cloudId is { } c && c.Site == site)
        {
            return c.CloudId;
        }

        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{site}/_edge/tenant_info"));
            var id = Str(doc.RootElement, "cloudId");
            if (id.Length > 0)
            {
                _cloudId = (site, id);
            }

            return id;
        }
        catch (Exception ex)
        {
            Log.Write($"Jira tenant_info: {ex.Message}");
            return string.Empty;
        }
    }

    private static List<JiraIssue> ParseIssues(string body)
    {
        var list = new List<JiraIssue>();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("issues", out var issues))
        {
            return list;
        }

        foreach (var i in issues.EnumerateArray())
        {
            var key = Str(i, "key");
            var f = i.GetProperty("fields");
            var status = Obj(f, "status");
            var sprint = string.Empty;
            if (f.TryGetProperty("customfield_10020", out var sp) && sp.ValueKind == JsonValueKind.Array)
            {
                sprint = sp.EnumerateArray().Select(x => Str(x, "name")).LastOrDefault(x => x.Length > 0) ?? string.Empty;
            }

            list.Add(new JiraIssue
            {
                Key = key,
                Summary = Str(f, "summary"),
                Status = Str(status, "name"),
                StatusCategory = Str(Obj(status, "statusCategory"), "key"),
                Priority = Str(Obj(f, "priority"), "name"),
                Type = Str(Obj(f, "issuetype"), "name"),
                Url = $"{JiraSite}/browse/{key}",
                Updated = Date(Str(f, "updated")),
                Created = Date(Str(f, "created")),
                Due = DateTime.TryParse(Str(f, "duedate"), out var due) ? due : null,
                Reporter = Str(Obj(f, "reporter"), "displayName"),
                Labels = f.TryGetProperty("labels", out var lb) && lb.ValueKind == JsonValueKind.Array ? lb.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray() : [],
                FixVersions = f.TryGetProperty("fixVersions", out var fv) && fv.ValueKind == JsonValueKind.Array ? fv.EnumerateArray().Select(x => Str(x, "name")).ToArray() : [],
                Sprint = sprint,
                Description = f.TryGetProperty("description", out var d) ? AdfToText(d, 1200) : string.Empty,
                Parent = Obj(f, "parent") is { ValueKind: JsonValueKind.Object } p ? $"{Str(p, "key")} {Str(Obj(p, "fields"), "summary")}".Trim() : string.Empty,
            });
        }

        return list;
    }

    /// <summary>Atlassian Document Format → plain text (paragraphs on new lines).</summary>
    private static string AdfToText(JsonElement node, int max)
    {
        var sb = new StringBuilder();
        void Walk(JsonElement n)
        {
            if (sb.Length > max || n.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (Str(n, "type") == "text")
            {
                sb.Append(Str(n, "text"));
            }

            if (n.TryGetProperty("content", out var kids) && kids.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in kids.EnumerateArray())
                {
                    Walk(k);
                }
            }

            if (Str(n, "type") is "paragraph" or "heading" or "listItem" or "codeBlock")
            {
                sb.Append('\n');
            }
        }

        Walk(node);
        var text = sb.ToString().Trim();
        return text.Length > max ? text[..max] + " …" : text;
    }

    // ---------------- Bitbucket ----------------

    private static async Task<(List<PullRequest>, List<PullRequest>, string?)> PollBitbucketAsync(bool force)
    {
        var cred = Secrets.Bitbucket();
        if (cred is null)
        {
            return ([], [], "No Bitbucket token (mcp.json bitbucket env)");
        }

        if (_bbUuid is null)
        {
            var (st, body) = await GetAsync($"{BitbucketApi}/user", cred);
            if (st != HttpStatusCode.OK || body is null)
            {
                return ([], [], $"Bitbucket {(int)st}");
            }

            using var doc = JsonDocument.Parse(body);
            _bbUuid = Str(doc.RootElement, "uuid");
        }

        var workspaces = await WorkspacesAsync(cred);
        var mine = new List<PullRequest>();
        foreach (var ws in workspaces)
        {
            mine.AddRange(await PagedPrsAsync($"{BitbucketApi}/workspaces/{ws}/pullrequests/{Uri.EscapeDataString(_bbUuid)}?state=OPEN&pagelen=50", cred));
        }

        await FillBuildsAsync(mine, cred);

        if (force || DateTime.UtcNow - _lastReviewScan > ReviewInterval)
        {
            _review = await ScanReviewsAsync(cred, workspaces, mine);
            await FillBuildsAsync(_review, cred);
            _lastReviewScan = DateTime.UtcNow;
            AnnounceNewReviews(_review);
        }

        return (mine.OrderByDescending(p => p.ReadyToMerge).ThenByDescending(p => p.Updated).ToList(), _review, null);
    }

    private static void AnnounceNewReviews(List<PullRequest> review)
    {
        var keys = review.Select(p => $"{p.Repo}#{p.Id}").ToHashSet();
        if (_seenReviews is not null)
        {
            foreach (var p in review.Where(p => !p.IsBot && !p.IApproved && !_seenReviews.Contains($"{p.Repo}#{p.Id}")))
            {
                NewReview?.Invoke(p);
            }
        }

        _seenReviews = keys;
    }

    private static async Task<List<string>> WorkspacesAsync(Credential cred)
    {
        if (_workspaces is not null)
        {
            return _workspaces;
        }

        var (st, body) = await GetAsync($"{BitbucketApi}/user/workspaces?pagelen=100", cred);
        var list = new List<string>();
        if (st == HttpStatusCode.OK && body is not null)
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var v in doc.RootElement.GetProperty("values").EnumerateArray())
            {
                list.Add(Str(Obj(v, "workspace"), "slug"));
            }
        }

        _workspaces = list;
        return list;
    }

    /// <summary>
    /// Bitbucket has no public "PRs I review" endpoint, so scan the working set:
    /// repos of my PRs (open + recently merged) and the most recently updated repos.
    /// </summary>
    private static async Task<List<PullRequest>> ScanReviewsAsync(Credential cred, List<string> workspaces, List<PullRequest> mine)
    {
        var repos = new HashSet<string>(mine.Select(p => p.Repo), StringComparer.OrdinalIgnoreCase);
        foreach (var ws in workspaces)
        {
            foreach (var p in await PagedPrsAsync($"{BitbucketApi}/workspaces/{ws}/pullrequests/{Uri.EscapeDataString(_bbUuid!)}?state=MERGED&pagelen=50", cred, maxPages: 1))
            {
                repos.Add(p.Repo);
            }

            var (st, body) = await GetAsync($"{BitbucketApi}/repositories/{ws}?role=contributor&sort=-updated_on&pagelen=25&fields=values.full_name", cred);
            if (st == HttpStatusCode.OK && body is not null)
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var v in doc.RootElement.GetProperty("values").EnumerateArray())
                {
                    repos.Add(Str(v, "full_name"));
                }
            }
        }

        var q = Uri.EscapeDataString($"state=\"OPEN\" AND reviewers.uuid=\"{_bbUuid}\"");
        var found = new ConcurrentBag<PullRequest>();
        await Parallel.ForEachAsync(repos, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (repo, _) =>
        {
            foreach (var p in await PagedPrsAsync($"{BitbucketApi}/repositories/{repo}/pullrequests?q={q}&pagelen=50", cred, maxPages: 2))
            {
                found.Add(p);
            }
        });
        _reviewRepos = repos.Count;
        Log.Write($"Review scan: {repos.Count} repos, {found.Count} PRs");
        return found.OrderBy(p => p.IApproved).ThenBy(p => p.IsBot).ThenByDescending(p => p.Updated).ToList();
    }

    /// <summary>
    /// Build status of the PR's head commit, the way Bitbucket shows it: latest run per build key,
    /// stopped runs that a newer run replaced are ignored, then the worst remaining state wins.
    /// One call per PR, cached until the head commit changes (running builds are re-checked).
    /// </summary>
    private static async Task FillBuildsAsync(List<PullRequest> prs, Credential cred)
    {
        await Parallel.ForEachAsync(prs, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (p, _) =>
        {
            var key = $"{p.Repo}#{p.Id}";
            if (!BuildCache.TryGetValue(key, out var cached) || cached.Head != p.HeadCommit || cached.Builds.Any(b => b.State == "INPROGRESS"))
            {
                var (st, body) = await GetAsync($"{BitbucketApi}/repositories/{p.Repo}/pullrequests/{p.Id}/statuses?pagelen=50", cred);
                var builds = new List<BuildRun>();
                if (st == HttpStatusCode.OK && body is not null)
                {
                    using var doc = JsonDocument.Parse(body);
                    var runs = doc.RootElement.GetProperty("values").EnumerateArray()
                        .Select(s => new
                        {
                            Key = Str(s, "key"),
                            Name = Str(s, "name"),
                            State = Str(s, "state"),
                            Url = Str(s, "url"),
                            Updated = Date(Str(s, "updated_on")),
                            Commit = Str(Obj(Obj(s, "links"), "commit"), "href").Split('/').LastOrDefault() ?? string.Empty,
                        })
                        .Where(s => p.HeadCommit.Length == 0 || s.Commit.Length == 0 || s.Commit.StartsWith(p.HeadCommit, StringComparison.OrdinalIgnoreCase) || p.HeadCommit.StartsWith(s.Commit, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Latest run per key.
                    var latest = runs.GroupBy(s => s.Key.Length > 0 ? s.Key : s.Name)
                        .Select(g => g.OrderByDescending(s => s.Updated).First())
                        .OrderByDescending(s => s.Updated)
                        .ToList();

                    // A STOPPED run is usually a superseded/cancelled one: drop it if any newer run finished.
                    var newestFinished = latest.Where(s => s.State is "SUCCESSFUL" or "FAILED" or "INPROGRESS").Select(s => s.Updated).DefaultIfEmpty().Max();
                    latest = latest.Where(s => s.State != "STOPPED" || s.Updated > newestFinished).ToList();
                    builds = latest.Select(s => new BuildRun(s.Name.Length > 0 ? s.Name : "Pipeline", s.State, s.Url, s.Updated)).ToList();
                }

                cached = (p.HeadCommit, builds);
                BuildCache[key] = cached;
            }

            p.Builds = cached.Builds;
            var worst = cached.Builds.OrderBy(b => b.State switch { "FAILED" => 0, "INPROGRESS" => 1, "STOPPED" => 2, _ => 3 }).FirstOrDefault();
            p.Build = worst?.State ?? string.Empty;
            p.BuildName = worst?.Name ?? string.Empty;
            p.BuildUrl = worst?.Url ?? string.Empty;
        });
    }

    private const string PrFields = "&fields=next,values.id,values.title,values.links.html.href,values.updated_on,values.created_on,values.comment_count,values.task_count,values.summary.raw,values.author.display_name,values.author.uuid,values.source.branch.name,values.source.commit.hash,values.destination.branch.name,values.destination.repository.full_name,values.participants.role,values.participants.approved,values.participants.state,values.participants.user.uuid,values.participants.user.display_name";

    private static async Task<List<PullRequest>> PagedPrsAsync(string url, Credential cred, int maxPages = 3)
    {
        var list = new List<PullRequest>();
        string? next = url + PrFields;
        for (var page = 0; next is not null && page < maxPages; page++)
        {
            var (st, body) = await GetAsync(next, cred);
            if (st != HttpStatusCode.OK || body is null)
            {
                break;
            }

            using var doc = JsonDocument.Parse(body);
            foreach (var v in doc.RootElement.GetProperty("values").EnumerateArray())
            {
                var reviewers = new List<Reviewer>();
                var iApproved = false;
                if (v.TryGetProperty("participants", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pt in parts.EnumerateArray())
                    {
                        var approved = pt.TryGetProperty("approved", out var a) && a.ValueKind == JsonValueKind.True;
                        var state = approved ? "approved" : Str(pt, "state");
                        var user = Obj(pt, "user");
                        if (approved && Str(user, "uuid") == _bbUuid)
                        {
                            iApproved = true;
                        }

                        if (Str(pt, "role") == "REVIEWER" || state.Length > 0)
                        {
                            reviewers.Add(new Reviewer(Str(user, "display_name"), Str(user, "uuid"), state));
                        }
                    }
                }

                list.Add(new PullRequest
                {
                    Repo = Str(Obj(Obj(v, "destination"), "repository"), "full_name"),
                    Id = v.GetProperty("id").GetInt32(),
                    Title = Str(v, "title"),
                    Author = Str(Obj(v, "author"), "display_name"),
                    AuthorUuid = Str(Obj(v, "author"), "uuid"),
                    SourceBranch = Str(Obj(Obj(v, "source"), "branch"), "name"),
                    HeadCommit = Str(Obj(Obj(v, "source"), "commit"), "hash"),
                    TargetBranch = Str(Obj(Obj(v, "destination"), "branch"), "name"),
                    Url = Str(Obj(Obj(v, "links"), "html"), "href"),
                    Description = Str(Obj(v, "summary"), "raw"),
                    Created = Date(Str(v, "created_on")),
                    Updated = Date(Str(v, "updated_on")),
                    Comments = Int(v, "comment_count"),
                    Tasks = Int(v, "task_count"),
                    Reviewers = reviewers.ToArray(),
                    IApproved = iApproved,
                });
            }

            next = doc.RootElement.TryGetProperty("next", out var n) ? n.GetString() : null;
        }

        return list;
    }

    // ---------------- HTTP / JSON helpers ----------------

    private static async Task<(HttpStatusCode, string?)> GetAsync(string url, Credential cred)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cred.User}:{cred.Secret}")));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await Http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"GET {new Uri(url).GetLeftPart(UriPartial.Path)} -> {(int)resp.StatusCode}");
            }

            return (resp.StatusCode, resp.IsSuccessStatusCode ? body : null);
        }
        catch (Exception ex)
        {
            Log.Write($"GET {new Uri(url).GetLeftPart(UriPartial.Path)} failed: {ex.Message}");
            return (0, null);
        }
    }

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static DateTimeOffset Date(string s) => DateTimeOffset.TryParse(s, out var d) ? d : default;
}
