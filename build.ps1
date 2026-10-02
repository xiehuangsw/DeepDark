# DeepDark build script
# Uses the C# compiler bundled with Windows (.NET Framework), no extra tooling required.
#
#   usage:  powershell -ExecutionPolicy Bypass -File build.ps1
#
# Output: dist/DeepDark.exe  (+ DeepDark.exe.config)

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'src\DeepDark.cs'
$icon   = Join-Path $root 'assets\DeepDark.ico'
$outDir = Join-Path $root 'dist'
$output = Join-Path $outDir 'DeepDark.exe'
$config = Join-Path $outDir 'DeepDark.exe.config'

if (-not (Test-Path $source)) { throw "source not found: $source" }
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework compiler) not found' }

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/codepage:65001'          # source is UTF-8: keeps the Chinese UI strings intact
    '/optimize+'
    '/platform:anycpu'
    "/out:$output"
    '/reference:System.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
)
if (Test-Path $icon) { $cscArgs += "/win32icon:$icon" }
$cscArgs += $source

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed, exit code $LASTEXITCODE" }

# Runtime config: disable concurrent GC — this is a tiny background tray app,
# a single-threaded workstation GC keeps the footprint slightly lower.
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <runtime>
    <gcServer enabled="false" />
    <gcConcurrent enabled="false" />
  </runtime>
</configuration>
'@ | Set-Content -Path $config -Encoding UTF8

Write-Host "OK -> $output" -ForegroundColor Green
