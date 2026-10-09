param(
    [Parameter(Mandatory = $true)][string]$PostgresBin,
    [int]$Port = 55432,
    [string]$Filter = ''
)
$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$runtimeRoot = Join-Path $workspace '.test-runtime'
$runRoot = Join-Path $runtimeRoot ('run-' + [Guid]::NewGuid().ToString('N'))
$dataPath = Join-Path $runRoot 'data'
$passwordPath = Join-Path $runRoot 'password.txt'
$logPath = Join-Path $runRoot 'postgres.log'
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$testPassword = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
Set-Content -LiteralPath $passwordPath -Value $testPassword -NoNewline
$oldTestConnection = $env:VOUCH_TEST_POSTGRES_ADMIN
$oldPgPassword = $env:PGPASSWORD
$started = $false
$testExit = 1
try {
    & (Join-Path $PostgresBin 'initdb.exe') -D $dataPath -U vouch_test_runner --pwfile=$passwordPath -A scram-sha-256 --encoding=UTF8 --locale=C
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the isolated PostgreSQL test server.' }
    # Start-Process -Wait also waits for the server child, which must outlive pg_ctl.
    $start = Start-Process -FilePath (Join-Path $PostgresBin 'pg_ctl.exe') -ArgumentList @('-D', ('"' + $dataPath + '"'), '-l', ('"' + $logPath + '"'), '-o', ('"-h 127.0.0.1 -p ' + $Port + '"'), '-w', 'start') -WindowStyle Hidden -PassThru
    $start.WaitForExit()
    if ($start.ExitCode -ne 0) { throw "Could not start the isolated test server on port $Port. Check $logPath." }
    $started = $true
    $env:PGPASSWORD = $testPassword
    & (Join-Path $PostgresBin 'psql.exe') -h 127.0.0.1 -p $Port -U vouch_test_runner -d postgres -v ON_ERROR_STOP=1 -c 'CREATE DATABASE vouch_test_admin;'
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the dedicated test admin database.' }
    $env:VOUCH_TEST_POSTGRES_ADMIN = "Host=127.0.0.1;Port=$Port;Database=vouch_test_admin;Username=vouch_test_runner;Password=$testPassword"
    $testArguments = @('test', (Join-Path $workspace 'Vouch.slnx'), '--configuration', 'Release', '--no-restore', '--verbosity', 'minimal')
    if ($Filter) { $testArguments += @('--filter', $Filter) }
    dotnet @testArguments
    $testExit = $LASTEXITCODE
}
finally {
    if ($started) {
        $stop = Start-Process -FilePath (Join-Path $PostgresBin 'pg_ctl.exe') -ArgumentList @('-D', ('"' + $dataPath + '"'), '-m', 'fast', '-w', 'stop') -WindowStyle Hidden -PassThru
        $stop.WaitForExit()
        if ($stop.ExitCode -ne 0) { Write-Warning "Test PostgreSQL did not shut down; inspect $logPath." }
    }
    $env:VOUCH_TEST_POSTGRES_ADMIN = $oldTestConnection
    $env:PGPASSWORD = $oldPgPassword
    Remove-Item -LiteralPath $passwordPath -ErrorAction SilentlyContinue
}
exit $testExit
