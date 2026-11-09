# Claude Dock

Dock widgets for the [PowerToys Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) Dock:

- **Claude sessions**: every running Claude Code session (Windows and WSL), with a colored status
  dot, a rich hover preview, and one click to jump to its terminal tab (Windows Terminal or Cursor/VS Code).
- **Work**: your assigned Jira issues, your open Bitbucket Cloud pull requests, and the PRs waiting
  for your review, with build state, approvals and authors.

> Community project. Not affiliated with or endorsed by Anthropic, Microsoft, or Atlassian.

## Claude sessions band

| Dot | Status |
|---|---|
| 🟠 orange `!` | needs you: permission prompt, question, or dialog |
| 🔵 blue | working |
| 🟢 green | idle |
| ⚪ gray | unknown |

- **Bar**: an overview item (`Claude 3`, or `1 need you`) plus one item per session.
- **Hover**: status and how long, what it wants to run, terminal, folder, branch, model, permission mode,
  context size, the last prompt, and the task list.
- **Click a session**: focuses its terminal.
  - Windows Terminal (native or WSL): selects the tab whose title matches the session title, via UI Automation.
  - Cursor / VS Code (local or Remote-WSL): the companion extension (`cursor-extension/`) shows the
    terminal whose process is in the session's process chain.
- **Click the overview**: a list grouped by *Needs you / Working / Idle*, with a details pane.
- **Label a session**: right-click → rename. Up to 45 characters. Saved in
  `~/.claude-dock/labels.json`.
- **Toasts**: a Windows notification when a session starts waiting for you.

## Work band (optional)

| Item | Shows |
|---|---|
| `Jira N · k!` | assigned issues that are not done; `k!` = high priority, overdue, or blocked |
| `PRs N · k✓ · k✗` | your open PRs; ready to merge / needs attention (changes requested or build failed) |
| `Review N +k🤖` | PRs where you are a reviewer; `+k🤖` = bot PRs (Renovate etc.) |

Each item has a hover summary. Click it for a grouped list with tags (author, approvals, build,
comments, tasks, age) and a details pane (reviewers, each build with a link, branches, linked Jira issue).

Build state uses only the PR's head commit and the newest run of each pipeline. A stopped run that a
newer run replaced does not count. "Ready to merge" is a heuristic (2 approvals, or all reviewers
approved, nothing blocking), not Bitbucket's merge checks.

### Setup

**Jira Cloud**: run this in your own terminal. The token goes to a hidden prompt and is stored in
Windows Credential Manager (`ClaudeDock:jira`). The site goes to `~/.claude-dock/config.json`.

```powershell
.\set-jira-token.ps1 -Site https://your-company.atlassian.net -Email you@your-company.com
```

Token: <https://id.atlassian.com/manage-profile/security/api-tokens>. Classic, or scoped with
`read:jira-work` and `read:jira-user`.

**Bitbucket Cloud**: the band reads, in this order:

1. Windows Credential Manager, generic credential `ClaudeDock:bitbucket` (user = Atlassian email,
   password = API token with Bitbucket read scopes).
2. The `bitbucket` MCP server env in `~/.cursor/mcp.json` (`BITBUCKET_USER_EMAIL`, `BITBUCKET_API_TOKEN`).

The review list scans your working set (repos of your recent PRs plus recently updated repos),
not every repo in a large workspace.

## Requirements

- Windows 11, PowerToys with Command Palette and its Dock enabled.
- [Claude Code](https://docs.anthropic.com/claude-code) with `claude agents --json` (Windows and/or WSL).
- .NET 10 SDK to build.
- Developer Mode on (simplest), or a self-signed certificate (see below).

## Build and install

```powershell
.\pack.ps1                         # build and register the package, restart Command Palette
.\add-to-dock.ps1                  # pin both bands to the Dock
.\cursor-extension\pack-vsix.ps1   # optional: Cursor companion extension
```

- **Developer Mode on**: `pack.ps1` registers the build folder directly. No signing.
- **Developer Mode off**: run `.\trust-cert.ps1` once (self-signed `CN=ClaudeDock`, current user only).
  `pack.ps1` then downloads `makeappx`/`signtool` from the `Microsoft.Windows.SDK.BuildTools` NuGet
  package into `tools\`, and packs, signs and installs an MSIX.

`pack.ps1` and `add-to-dock.ps1` stop Command Palette **and all its extension processes**, then start
Command Palette again. Stopping only the UI leaves extensions linked to the dead instance, and their
bands stop updating.

`add-to-dock.ps1 -Section StartBands|CenterBands|EndBands` picks where new bands go. Bands that are
already on the Dock stay where you put them. It backs up `settings.json` first.

## Diagnostics

```powershell
dotnet build ClaudeDock\ClaudeDock.csproj -c Debug -p:Platform=x64 -o out\cli
.\out\cli\ClaudeDock.exe --dump | Out-String                 # sessions as the band sees them
.\out\cli\ClaudeDock.exe --work | Out-String                 # Jira and Bitbucket
.\out\cli\ClaudeDock.exe --focus <sessionIdPrefix> | Out-String
```

Log: `~/.claude-dock/claudedock.log`.

## How it works

- **Status**: polls `claude agents --json` every 4 s on Windows and in each running WSL distro
  (`docker-desktop` is skipped). Details come from the tail of each session's transcript
  (`~/.claude/projects/<project>/<sessionId>.jsonl`).
- **Host detection**: Windows process ancestry (`WindowsTerminal.exe`, `Cursor.exe`, `Code.exe`).
  For WSL, the session's `/proc/<pid>/environ` (`TERM_PROGRAM=vscode` → Cursor, `WT_SESSION` → Windows Terminal).
- **Cursor handshake**: `~/.claude-dock/focus-request.json` → `focus-response-<id>.json`.
- **Hover**: the Dock shows an item's title and subtitle as its tooltip, so the subtitle holds the
  preview. `add-to-dock.ps1` hides subtitles on the bar.

## Known limits

- Windows Terminal tab matching is by title. Two sessions with the same title can't be told apart,
  and a new session has no title until Claude names it. With split panes, the tab is selected, not the pane.
- Hover text is plain text. Colors and layout appear only in the click flyout.
- Status lags by up to ~5 s.
- Background sessions older than 24 h are hidden.

## License

[MIT](LICENSE)
