using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ClaudeDock;

public sealed record TodoItem(string Content, string Status);

/// <summary>What we can learn about a session from the tail of its transcript (.jsonl).</summary>
public sealed record TranscriptInfo
{
    public static readonly TranscriptInfo Empty = new();

    public string Title { get; init; } = string.Empty;

    public string GitBranch { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string PermissionMode { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string LastPrompt { get; init; } = string.Empty;

    public string LastReply { get; init; } = string.Empty;

    /// <summary>The newest tool call that has no result yet (what a permission prompt is about).</summary>
    public string PendingTool { get; init; } = string.Empty;

    public string PendingToolDetail { get; init; } = string.Empty;

    public long ContextTokens { get; init; }

    public long OutputTokens { get; init; }

    public DateTimeOffset LastActivity { get; init; }

    public int ToolCalls { get; init; }

    public IReadOnlyList<TodoItem> Todos { get; init; } = [];
}

internal static class TranscriptReader
{
    private const int TailBytes = 768 * 1024;

    public static TranscriptInfo Read(string path)
    {
        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > TailBytes)
            {
                fs.Seek(-TailBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(fs, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Write($"Transcript {path}: {ex.Message}");
            return TranscriptInfo.Empty;
        }

        var lines = text.Split('\n');
        string? title = null, customTitle = null, branch = null, model = null, mode = null, version = null;
        string? prompt = null, reply = null, pendingTool = null, pendingDetail = null;
        long ctx = -1, outTok = 0;
        DateTimeOffset last = default;
        List<TodoItem>? todos = null;
        var resolvedToolIds = new HashSet<string>();
        var toolCalls = 0;

        // Newest first. The first (partial) line of the tail is skipped by the parse guard.
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Length < 2 || line[0] != '{')
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch
            {
                continue;
            }

            using (doc)
            {
                var r = doc.RootElement;
                var type = Str(r, "type");
                customTitle ??= NonEmpty(Str(r, "customTitle"));
                title ??= NonEmpty(Str(r, "aiTitle"));
                branch ??= NonEmpty(Str(r, "gitBranch"));
                mode ??= NonEmpty(Str(r, "permissionMode"));
                version ??= NonEmpty(Str(r, "version"));
                prompt ??= CleanPrompt(Str(r, "lastPrompt"));
                if (last == default && DateTimeOffset.TryParse(Str(r, "timestamp"), out var ts))
                {
                    last = ts;
                }

                if (!r.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var content = msg.TryGetProperty("content", out var c) ? c : default;
                if (type == "assistant")
                {
                    model ??= NonEmpty(Str(msg, "model"));
                    if (ctx < 0 && msg.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        ctx = Num(u, "input_tokens") + Num(u, "cache_read_input_tokens") + Num(u, "cache_creation_input_tokens");
                        outTok = Num(u, "output_tokens");
                    }

                    if (content.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var part in content.EnumerateArray())
                    {
                        var ptype = Str(part, "type");
                        if (ptype == "text" && reply is null)
                        {
                            reply = NonEmpty(Str(part, "text"));
                        }
                        else if (ptype == "tool_use")
                        {
                            toolCalls++;
                            var name = Str(part, "name");
                            var input = part.TryGetProperty("input", out var inp) ? inp : default;
                            if (todos is null && name == "TodoWrite" && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("todos", out var td) && td.ValueKind == JsonValueKind.Array)
                            {
                                todos = td.EnumerateArray().Select(t => new TodoItem(Str(t, "content"), Str(t, "status"))).ToList();
                            }

                            if (pendingTool is null && !resolvedToolIds.Contains(Str(part, "id")))
                            {
                                pendingTool = name;
                                pendingDetail = DescribeToolInput(input);
                            }
                        }
                    }
                }
                else if (type == "user")
                {
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        prompt ??= CleanPrompt(content.GetString());
                    }
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            var ptype = Str(part, "type");
                            if (ptype == "tool_result")
                            {
                                resolvedToolIds.Add(Str(part, "tool_use_id"));
                            }
                            else if (ptype == "text")
                            {
                                prompt ??= CleanPrompt(Str(part, "text"));
                            }
                        }
                    }
                }
            }

            if (title is not null && branch is not null && model is not null && prompt is not null && reply is not null && ctx >= 0 && pendingTool is not null && todos is not null)
            {
                break;
            }
        }

        return new TranscriptInfo
        {
            Title = customTitle ?? title ?? string.Empty,
            GitBranch = branch is null or "HEAD" ? string.Empty : branch,
            Model = model ?? string.Empty,
            PermissionMode = mode ?? string.Empty,
            Version = version ?? string.Empty,
            LastPrompt = prompt ?? string.Empty,
            LastReply = reply ?? string.Empty,
            PendingTool = pendingTool ?? string.Empty,
            PendingToolDetail = pendingDetail ?? string.Empty,
            ContextTokens = Math.Max(ctx, 0),
            OutputTokens = outTok,
            LastActivity = last,
            ToolCalls = toolCalls,
            Todos = todos ?? [],
        };
    }

    private static string DescribeToolInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "description", "command", "file_path", "pattern", "url", "query", "prompt", "skill" })
        {
            var v = Str(input, key);
            if (v.Length > 0)
            {
                return v;
            }
        }

        return string.Empty;
    }

    /// <summary>Skips harness-injected messages (system reminders, command wrappers).</summary>
    private static string? CleanPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.StartsWith('<') || t.StartsWith("[SYSTEM", StringComparison.Ordinal) || t.StartsWith("Caveat:", StringComparison.Ordinal)
            || t.StartsWith("[Request interrupted", StringComparison.Ordinal) || t.StartsWith("Another Claude session", StringComparison.Ordinal)
            || t.StartsWith("This session is being continued", StringComparison.Ordinal) || t.Contains("<agent-message", StringComparison.Ordinal)
            || t.Contains("<task-notification>", StringComparison.Ordinal))
        {
            return null;
        }

        return t;
    }

    private static string? NonEmpty(string s) => s.Length > 0 ? s : null;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
