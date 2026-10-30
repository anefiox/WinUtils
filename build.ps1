# Publishes each tool as its own Native AOT exe and zip, so tools can be downloaded separately:
#   publish\<tool>\<tool>.exe            the standalone exe
#   publish\<tool>-<version>-<rid>.zip   the release download
# Usage: .\build.ps1 [-Tool grep] [-Runtime win-x64]
param([string]$Tool, [string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'

# The AOT linker step shells out to vswhere.exe, which the VS installer doesn't put on PATH.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }

$version = "$(([xml](Get-Content "$PSScriptRoot\Directory.Build.props")).Project.PropertyGroup.Version)".Trim()

Get-ChildItem "$PSScriptRoot\src\*\*.csproj" | ForEach-Object {
    $props = ([xml](Get-Content $_)).Project.PropertyGroup
    if ("$($props.OutputType)".Trim() -ne 'Exe') { return }
    $name = "$($props.AssemblyName)".Trim()
    if ($Tool -and $name -ne $Tool) { return }

    $out = "$PSScriptRoot\publish\$name"
    dotnet publish $_.FullName -c Release -r $Runtime -o $out -p:DebugType=none -p:NativeDebugSymbols=false
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Compress-Archive -Path "$out\$name.exe" -DestinationPath "$PSScriptRoot\publish\$name-$version-$Runtime.zip" -Force
}
