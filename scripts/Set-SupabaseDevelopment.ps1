#Requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-zA-Z0-9.-]+$')][string]$DatabaseHost,
    [Parameter(Mandatory = $true)][string]$DatabaseUser,
    [ValidateRange(1, 65535)][int]$Port = 5432,
    [string]$Database = 'postgres',
    [string]$RootCertificate,
    [switch]$PromptForPassword,
    [System.Security.SecureString]$Password,
    [string]$LocalSettingsPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src/Vouch.Api/appsettings.Local.json')
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $LocalSettingsPath -PathType Leaf)) {
    throw 'Create appsettings.Local.json with your existing JWT/encryption/lookup keys using the README first. This command never generates or rotates those keys.'
}
$settings = Get-Content -Raw -LiteralPath $LocalSettingsPath | ConvertFrom-Json -AsHashtable
# This command configures only database connectivity. Application secrets can also
# come from environment settings; validate them when starting the API, not here.
if ($RootCertificate -and !(Test-Path -LiteralPath $RootCertificate -PathType Leaf)) {
    throw 'Root certificate file was not found. No settings were changed.'
}
if (!$Password -and !$PromptForPassword -and $settings.ConnectionStrings.DefaultConnection) {
    $existingConnection = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        $existingConnection.set_ConnectionString($settings.ConnectionStrings.DefaultConnection)
        # Reuse credentials only for the same destination/account. Certificate updates
        # should not require entering an already-saved password again.
        if ($existingConnection.ContainsKey('Host') -and $existingConnection.ContainsKey('Username') -and
            $existingConnection.ContainsKey('Port') -and $existingConnection.ContainsKey('Database') -and
            $existingConnection.ContainsKey('Password') -and
            $existingConnection.get_Item('Host') -eq $DatabaseHost -and
            $existingConnection.get_Item('Username') -ceq $DatabaseUser -and
            [int]$existingConnection.get_Item('Port') -eq $Port -and
            $existingConnection.get_Item('Database') -ceq $Database) {
            $Password = ConvertTo-SecureString ([string]$existingConnection.get_Item('Password')) -AsPlainText -Force
            if (!$RootCertificate -and $existingConnection.ContainsKey('Root Certificate')) {
                $RootCertificate = [string]$existingConnection.get_Item('Root Certificate')
            }
        }
    }
    catch { $Password = $null } # Malformed old settings fall back to hidden password entry.
    finally { $existingConnection = $null }
}
if (!$Password) { $Password = Read-Host 'Supabase database password (hidden; not an API key)' -AsSecureString }
if ($Password.Length -eq 0) { throw 'Database password must not be empty. No settings were changed.' }
$plainPassword = [System.Net.NetworkCredential]::new('', $Password).Password
try {
    # A builder safely quotes passwords containing semicolons, quotes or equals signs.
    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connection['Host'] = $DatabaseHost
    $connection['Port'] = $Port
    $connection['Database'] = $Database
    $connection['Username'] = $DatabaseUser
    $connection['Password'] = $plainPassword
    $connection['SSL Mode'] = 'VerifyFull'
    $connection['Maximum Pool Size'] = 10
    $connection['Minimum Pool Size'] = 0
    $connection['Timeout'] = 15
    if ($RootCertificate) { $connection['Root Certificate'] = [IO.Path]::GetFullPath($RootCertificate) }
    if (!$settings.ConnectionStrings) { $settings.ConnectionStrings = @{} }
    $settings.ConnectionStrings.DefaultConnection = $connection.ConnectionString
    $json = $settings | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($LocalSettingsPath), $json + [Environment]::NewLine)
}
finally { $plainPassword = $null; $connection = $null; $json = $null }
Write-Host 'Development database configuration saved. Existing JWT/encryption keys preserved. No database connection or migration was performed.'
