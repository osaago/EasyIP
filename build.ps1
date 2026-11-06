# Builds EasyIP with the C# compiler that ships inside Windows. No SDK needed.
#   .\build.ps1             app + unit tests + installer (installer needs Inno Setup 6)
#   .\build.ps1 -NoInstaller  skip the installer
param([switch]$NoInstaller)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fx\csc.exe"
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$refs = @(
    "$fx\WPF\PresentationFramework.dll", "$fx\WPF\PresentationCore.dll", "$fx\WPF\WindowsBase.dll",
    "$fx\System.Xaml.dll", "$fx\System.Management.dll", "$fx\System.Runtime.Serialization.dll",
    "$fx\System.Xml.dll", "$fx\System.Core.dll", "$fx\System.dll"
) | ForEach-Object { "/r:$_" }

$common = @('/nologo', '/optimize+', '/platform:anycpu', '/warn:4', '/nowarn:1607,1701,1702', '/utf8output', '/codepage:65001') + $refs
$core = 'IpValidator.cs', 'Models.cs', 'Storage.cs', 'NetworkService.cs' | ForEach-Object { Join-Path $root "src\$_" }
$ui = 'MainView.cs', 'Program.cs' | ForEach-Object { Join-Path $root "src\$_" }
$xaml = Join-Path $root 'src\MainWindow.xaml'
$ico = Join-Path $root 'assets\EasyIP.ico'
$resources = "/resource:$xaml,MainWindow.xaml", "/resource:$ico,EasyIP.ico"

function Compile([string]$label, [string[]]$arguments) {
    Write-Host "== $label"
    & $csc @arguments | Where-Object { $_ -notmatch 'C# 5|This compiler is provided|https?://go\.microsoft|^\s*$' }
    if ($LASTEXITCODE -ne 0) { throw "$label failed" }
}

# The real app asks for administrator rights.
Compile 'EasyIP.exe' ($common + $resources + @(
    '/target:winexe', "/out:$out\EasyIP.exe", "/win32icon:$ico", "/win32manifest:$root\src\app.manifest") + $core + $ui)

# Same app without the admin request, used only to look at the UI during development.
$dev = Join-Path $out 'dev'
New-Item -ItemType Directory -Force $dev | Out-Null
$manifest = (Get-Content "$root\src\app.manifest" -Raw) -replace 'requireAdministrator', 'asInvoker'
Set-Content "$dev\dev.manifest" $manifest -Encoding UTF8
Compile 'EasyIP-dev.exe' ($common + $resources + @(
    '/target:winexe', "/out:$dev\EasyIP-dev.exe", "/win32icon:$ico", "/win32manifest:$dev\dev.manifest") + $core + $ui)

Compile 'Tests.exe' ($common + @('/target:exe', "/out:$dev\Tests.exe", '/main:EasyIP.Tests.Tests') + $core + "$root\tests\Tests.cs")
Write-Host '== Unit tests'
& "$dev\Tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed' }

if (-not $NoInstaller) {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 not found. Install it or use -NoInstaller.' }
    Write-Host '== Installer'
    & $iscc /Q "$root\installer\EasyIP.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    Get-ChildItem "$root\dist" | Select-Object Name, Length | Format-Table -AutoSize
}
