using System;
using System.IO;
using System.Text.Json;

namespace ClaudeDock.Work;

/// <summary>
/// User settings in ~/.claude-dock/config.json (written by set-jira-token.ps1), for example:
/// <code>{ "jiraSite": "https://your-company.atlassian.net" }</code>
/// The environment variable CLAUDEDOCK_JIRA_SITE overrides the file.
/// </summary>
internal static class Config
{
    private static readonly string FilePath = Path.Combine(DataDir.Path, "config.json");
    private static (DateTime Stamp, string JiraSite) _cache = (DateTime.MinValue, string.Empty);

    /// <summary>Jira Cloud site URL without a trailing slash, or empty when not set.</summary>
    public static string JiraSite
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CLAUDEDOCK_JIRA_SITE");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env.Trim().TrimEnd('/');
            }

            try
            {
                if (!File.Exists(FilePath))
                {
                    return string.Empty;
                }

                var stamp = File.GetLastWriteTimeUtc(FilePath);
                if (stamp != _cache.Stamp)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(FilePath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    var site = doc.RootElement.TryGetProperty("jiraSite", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
                    _cache = (stamp, site.Trim().TrimEnd('/'));
                }

                return _cache.JiraSite;
            }
            catch (Exception ex)
            {
                Log.Write($"Read config.json: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
