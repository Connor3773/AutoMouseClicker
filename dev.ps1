$ErrorActionPreference = 'Stop'

$sdkRoot = Join-Path $PSScriptRoot '.tools\dotnet'
$dotnet = Join-Path $sdkRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $systemDotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $systemDotnet) {
        throw 'Install the SDK from global.json on PATH or into .tools\dotnet first.'
    }
    $dotnet = $systemDotnet.Source
    $sdkRoot = Split-Path -Parent $dotnet
}

$env:DOTNET_ROOT = $sdkRoot
$env:DOTNET_ROOT_X64 = $sdkRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot '.tools\nuget-http-cache'
$env:NUGET_SCRATCH = Join-Path $PSScriptRoot '.tools\nuget-scratch'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $PSScriptRoot '.tools\nuget-plugins-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_NOLOGO = '1'
$env:PATH = "$sdkRoot;$env:PATH"

Push-Location $PSScriptRoot
try {
    if ($args.Count -eq 0) {
        & $dotnet build 'Ming-AutoClicker.csproj' --configuration Debug
    }
    else {
        & $dotnet @args
    }
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
exit $exitCode
