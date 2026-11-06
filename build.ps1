$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $build,$dist | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Requires the Windows .NET Framework 4.x C# compiler.' }
$zip = Join-Path $root 'vendor\windows-debug-tools.zip'
if (-not (Test-Path -LiteralPath $zip)) { throw 'Missing vendor/windows-debug-tools.zip' }
$references = @('System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Web.Extensions.dll')
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',('/out:'+(Join-Path $dist 'SymbolTool.exe')),('/resource:'+$zip+',tools.zip'))
foreach ($reference in $references) { $arguments += '/reference:' + $reference }
$arguments += Join-Path $root 'src\SymbolTool.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
# A console build runs the same entry point for repeatable automated verification.
$arguments = $arguments | ForEach-Object { if ($_ -eq '/target:winexe') { '/target:exe' } elseif ($_.StartsWith('/out:')) { '/out:'+(Join-Path $build 'SymbolTool.Cli.exe') } else { $_ } }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }
Copy-Item -LiteralPath (Join-Path $root 'vendor\NOTICE.txt') -Destination (Join-Path $dist 'THIRD-PARTY-NOTICE.txt') -Force
Get-FileHash -LiteralPath (Join-Path $dist 'SymbolTool.exe') -Algorithm SHA256 | ForEach-Object { $_.Hash + '  SymbolTool.exe' } | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
Write-Host ('Built: '+(Join-Path $dist 'SymbolTool.exe'))
