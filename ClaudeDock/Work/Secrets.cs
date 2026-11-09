using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClaudeDock.Work;

internal sealed record Credential(string User, string Secret);

/// <summary>
/// Secrets are read at runtime, never copied:
///  - Bitbucket: Windows Credential Manager "ClaudeDock:bitbucket", else the "bitbucket" server env in ~/.cursor/mcp.json.
///  - Jira:      Windows Credential Manager "ClaudeDock:jira" (see set-jira-token.ps1).
/// </summary>
internal static class Secrets
{
    public const string JiraTarget = "ClaudeDock:jira";
    public const string BitbucketTarget = "ClaudeDock:bitbucket";

    public static Credential? Bitbucket() => ReadCredential(BitbucketTarget) ?? FromCursorMcp();

    public static Credential? Jira() => ReadCredential(JiraTarget);

    private static Credential? FromCursorMcp()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "mcp.json");
            if (!File.Exists(path))
            {
                return null;
            }

            // Cursor's mcp.json is JSONC (comments, trailing commas).
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("mcpServers", out var servers)
                && servers.TryGetProperty("bitbucket", out var bb)
                && bb.TryGetProperty("env", out var env)
                && env.TryGetProperty("BITBUCKET_USER_EMAIL", out var email)
                && env.TryGetProperty("BITBUCKET_API_TOKEN", out var token))
            {
                return new Credential(email.GetString() ?? string.Empty, token.GetString() ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Read mcp.json: {ex.Message}");
        }

        return null;
    }

    // ---- Windows Credential Manager ----
    private const int CRED_TYPE_GENERIC = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static Credential? ReadCredential(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            var secret = cred.CredentialBlobSize > 0 ? Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2) : string.Empty;
            return new Credential(cred.UserName ?? string.Empty, secret);
        }
        finally
        {
            CredFree(ptr);
        }
    }
}
