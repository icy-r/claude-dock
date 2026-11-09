# Sets up the Jira widget:
#   - saves your Jira site to ~/.claude-dock/config.json ("jiraSite")
#   - stores your Jira API token in Windows Credential Manager as "ClaudeDock:jira"
#     (your user only, encrypted by Windows)
# Run it in your OWN terminal: the token is typed at a hidden prompt and never lands in a file,
# the command line, or shell history.
#
#   .\set-jira-token.ps1 -Site https://your-company.atlassian.net -Email you@your-company.com
#
# Create the token at https://id.atlassian.com/manage-profile/security/api-tokens
#   - "Create API token" (classic), or
#   - "Create API token with scopes" -> app Jira -> scopes: read:jira-work, read:jira-user
#
# Remove later: cmdkey /delete:ClaudeDock:jira
param(
    [Parameter(Mandatory)][string]$Site,
    [Parameter(Mandatory)][string]$Email
)
$ErrorActionPreference = "Stop"

$Site = $Site.Trim().TrimEnd('/')
if ($Site -notmatch '^https://') { $Site = "https://$Site" }

# Merge into config.json, keeping any other settings.
$dir = Join-Path $env:USERPROFILE ".claude-dock"
New-Item -ItemType Directory -Force $dir | Out-Null
$configPath = Join-Path $dir "config.json"
$config = if (Test-Path $configPath) { [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($configPath)) } else { [System.Text.Json.Nodes.JsonObject]::new() }
$config["jiraSite"] = [System.Text.Json.Nodes.JsonValue]::Create($Site)
$opts = [System.Text.Json.JsonSerializerOptions]::new(); $opts.WriteIndented = $true
[IO.File]::WriteAllText($configPath, $config.ToJsonString($opts))

Add-Type -Namespace ClaudeDockCred -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct CREDENTIAL {
    public int Flags; public int Type; public string TargetName; public string Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist;
    public int AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName;
}
[DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool CredWrite(ref CREDENTIAL credential, int flags);
'@

$secure = Read-Host "Jira API token for $Email" -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secure)
try {
    $cred = New-Object ClaudeDockCred.Native+CREDENTIAL
    $cred.Type = 1                 # CRED_TYPE_GENERIC
    $cred.Persist = 2              # CRED_PERSIST_LOCAL_MACHINE (this user, survives reboot)
    $cred.TargetName = "ClaudeDock:jira"
    $cred.UserName = $Email
    $cred.CredentialBlob = $ptr
    $cred.CredentialBlobSize = $secure.Length * 2
    if (-not [ClaudeDockCred.Native]::CredWrite([ref]$cred, 0)) {
        throw "CredWrite failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($ptr)
}
Write-Host "Saved $Site and the token. The dock picks them up on its next refresh (or click Refresh in the Jira list)."
