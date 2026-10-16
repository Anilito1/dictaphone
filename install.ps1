# Builds Dictee and installs it to %LOCALAPPDATA%\Programs\Dictee, then starts it.
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Dictee'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'

Get-Process Dictee -ErrorAction SilentlyContinue | Stop-Process -Force
& $dotnet publish (Join-Path $PSScriptRoot 'Dictee.csproj') -c Release -o $target --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Start-Process (Join-Path $target 'Dictee.exe')
