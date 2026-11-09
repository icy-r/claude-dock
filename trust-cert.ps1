# One-time setup: creates a self-signed code-signing certificate "CN=ClaudeDock" in your
# user store and trusts it for YOUR USER ONLY (no admin), so Windows will install the
# locally built ClaudeDock.msix.
#
#   -Store TrustedPeople  (default) narrowest trust; tried first.
#   -Store Root           use only if install still fails with 0x800B0109 (untrusted root).
#                         Windows shows a confirmation dialog for this one.
#
# Undo: Get-ChildItem Cert:\CurrentUser\My, Cert:\CurrentUser\TrustedPeople, Cert:\CurrentUser\Root |
#         Where-Object Subject -eq 'CN=ClaudeDock' | Remove-Item
param([ValidateSet("TrustedPeople", "Root")][string]$Store = "TrustedPeople")
$ErrorActionPreference = "Stop"

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=ClaudeDock' -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=ClaudeDock" -KeyUsage DigitalSignature `
        -FriendlyName "ClaudeDock local signing" -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") -NotAfter (Get-Date).AddYears(5)
}

$cer = Join-Path $PSScriptRoot "out\ClaudeDock.cer"
New-Item -ItemType Directory -Force (Split-Path $cer) | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\CurrentUser\$Store" | Out-Null
Write-Host "Trusted CN=ClaudeDock ($($cert.Thumbprint)) in CurrentUser\$Store"
