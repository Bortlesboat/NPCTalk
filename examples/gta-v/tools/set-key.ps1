# Save an API key to the Windows user environment without it showing anywhere: paste it at the prompt.
#   powershell -ExecutionPolicy Bypass -File tools\set-key.ps1 ELEVENLABS_API_KEY
param([Parameter(Mandatory)][string]$Name)

$secure = Read-Host "Paste your $Name and press Enter (it won't be shown)" -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
if (-not $plain) { Write-Error "Nothing entered; $Name not changed."; exit 1 }
[Environment]::SetEnvironmentVariable($Name, $plain, 'User')
"Saved $Name ($($plain.Length) characters). You can close this tab."
