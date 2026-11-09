using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;

namespace ClaudeDock;

/// <summary>Brings the terminal that runs a Claude session to the front.</summary>
internal static class Focuser
{
    public static string Focus(ClaudeSession s)
    {
        try
        {
            var result = s.Host switch
            {
                TerminalHost.Cursor => FocusCursor(s),
                _ => FocusWindowsTerminal(s),
            };
            Log.Write($"Focus {s.Source}/{s.Pid} '{s.DisplayTitle}' host={s.Host}: {result}");
            return result;
        }
        catch (Exception ex)
        {
            Log.Write($"Focus failed: {ex}");
            return "Error: " + ex.Message;
        }
    }

    // ---------- Windows Terminal ----------

    private static string FocusWindowsTerminal(ClaudeSession s)
    {
        var windows = NativeWindow.TopLevelWindows()
            .Where(w => NativeWindow.GetClass(w.Hwnd) == "CASCADIA_HOSTING_WINDOW_CLASS")
            .ToList();
        if (windows.Count == 0)
        {
            return "No Windows Terminal window found";
        }

        var wanted = s.DisplayTitle.Trim();
        (nint Hwnd, AutomationElement Tab, int Score)? best = null;
        var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

        foreach (var w in windows)
        {
            AutomationElement root;
            try
            {
                root = AutomationElement.FromHandle(w.Hwnd);
            }
            catch
            {
                continue;
            }

            foreach (AutomationElement tab in root.FindAll(TreeScope.Descendants, tabCondition))
            {
                var name = StripStatusGlyph(tab.Current.Name);
                var score = Score(name, wanted);
                if (score > 0 && (best is null || score > best.Value.Score))
                {
                    best = (w.Hwnd, tab, score);
                }
            }
        }

        if (best is null)
        {
            NativeWindow.BringToFront(windows[0].Hwnd);
            return $"Tab '{wanted}' not found; opened Windows Terminal";
        }

        if (best.Value.Tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p))
        {
            ((SelectionItemPattern)p).Select();
        }

        NativeWindow.BringToFront(best.Value.Hwnd);
        return "OK";
    }

    /// <summary>Claude Code prefixes the terminal title with a status glyph ("✳ ", "◑ " ...).</summary>
    internal static string StripStatusGlyph(string name)
    {
        name = name.Trim();
        var space = name.IndexOf(' ');
        if (space > 0 && space <= 3 && !name[..space].Any(char.IsLetterOrDigit))
        {
            return name[(space + 1)..].Trim();
        }

        return name;
    }

    private static int Score(string tabName, string wanted)
    {
        if (wanted.Length == 0)
        {
            return 0;
        }

        if (string.Equals(tabName, wanted, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (tabName.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return tabName.Contains(wanted, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    // ---------- Cursor / VS Code ----------

    private static string FocusCursor(ClaudeSession s)
    {
        var id = Guid.NewGuid().ToString("N");
        var request = Path.Combine(DataDir.Path, "focus-request.json");
        var response = Path.Combine(DataDir.Path, $"focus-response-{id}.json");
        var json = JsonSerializer.Serialize(
            new FocusRequest
            {
                Id = id,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Source = s.Source,
                Pids = s.ProcessChain,
            },
            FocusJsonContext.Default.FocusRequest);
        File.WriteAllText(request, json);

        string? workspace = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2500)
        {
            if (File.Exists(response))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(response));
                    workspace = doc.RootElement.TryGetProperty("workspaceName", out var wn) ? wn.GetString() : string.Empty;
                }
                catch
                {
                    Thread.Sleep(50);
                    continue;
                }

                TryDelete(response);
                break;
            }

            Thread.Sleep(50);
        }

        var editorPids = Process.GetProcessesByName("Cursor").Concat(Process.GetProcessesByName("Code"))
            .Select(p => p.Id).ToHashSet();
        var editorWindows = NativeWindow.TopLevelWindows()
            .Where(w => editorPids.Contains(w.Pid) && (w.Title.EndsWith("Cursor", StringComparison.Ordinal) || w.Title.EndsWith("Visual Studio Code", StringComparison.Ordinal)))
            .ToList();

        if (editorWindows.Count == 0)
        {
            return "No Cursor window found";
        }

        var target = editorWindows.FirstOrDefault(w => !string.IsNullOrEmpty(workspace) && w.Title.Contains(workspace, StringComparison.OrdinalIgnoreCase));
        if (target.Hwnd == 0)
        {
            // Fall back to the folder name from the session cwd.
            var folder = s.Cwd.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? string.Empty;
            target = editorWindows.FirstOrDefault(w => folder.Length > 0 && w.Title.Contains(folder, StringComparison.OrdinalIgnoreCase));
        }

        if (target.Hwnd == 0 && editorWindows.Count == 1)
        {
            target = editorWindows[0];
        }

        if (target.Hwnd == 0)
        {
            return "Cursor window not found";
        }

        NativeWindow.BringToFront(target.Hwnd);
        return workspace is null ? "Opened Cursor (companion extension did not answer; terminal not selected)" : "OK";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}

internal sealed class FocusRequest
{
    public string Id { get; set; } = string.Empty;

    public long Ts { get; set; }

    public string Source { get; set; } = string.Empty;

    public int[] Pids { get; set; } = [];
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(FocusRequest))]
internal sealed partial class FocusJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
