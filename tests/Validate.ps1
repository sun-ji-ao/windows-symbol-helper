param([switch]$Online)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $root 'build\SymbolTool.Cli.exe'
$results = [Collections.Generic.List[string]]::new()
function Run-Check([string[]]$Arguments, [int]$Expected = 0) {
    $output = & $cli @Arguments 2>&1
    if ($LASTEXITCODE -ne $Expected) { throw "Unexpected exit $LASTEXITCODE (expected $Expected): $output" }
    return $output
}
Run-Check @('--self-test') | Out-Null
$results.Add('PASS retry exhaustion: initial attempt + 3 retries; early success; exceptions; cancellation; malformed manifest; corrupt PDB')
Run-Check @('--ui-preview',(Join-Path $root 'build\ui-preview.png')) | Out-Null
$results.Add('PASS Windows Forms initialization and bitmap rendering')
if ($Online) {
    Run-Check @('--collect-file',(Join-Path $env:WINDIR 'System32\version.dll')) | Out-Null
    $request = Get-ChildItem (Join-Path $root 'build') -Directory -Filter 'request_*' | Sort-Object Name -Descending | Select-Object -First 1
    $manifest = Join-Path $request.FullName 'symbols.manifest'
    $cache = Join-Path $root ('build\test-cache-'+(Get-Date -Format 'yyyyMMdd_HHmmss_fff'))
    Run-Check @('--download',$manifest,$cache) | Out-Null
    Run-Check @('--verify',$cache) | Out-Null
    $results.Add('PASS real Microsoft public PDB download; GUID + Age; SHA-256 offline verification')
    Run-Check @('--download',$manifest,$cache) | Out-Null
    $report = Get-ChildItem (Join-Path $cache 'reports') -Directory | Sort-Object Name -Descending | Select-Object -First 1
    $data = Get-Content (Join-Path $report.FullName 'report.json') -Raw | ConvertFrom-Json
    if ($data.Results[0].Status -ne 'CachedVerified' -or $data.Results[0].Attempts -ne 0) { throw 'Cache reuse failed' }
    $results.Add('PASS verified cache reuse without network downloads')
    $pdb = Get-ChildItem $cache -Recurse -Filter '*.pdb' -File | Select-Object -First 1
    $original = [IO.File]::ReadAllBytes($pdb.FullName)
    $stream = [IO.File]::Open($pdb.FullName,[IO.FileMode]::Append)
    try { $stream.WriteByte(0x55) } finally { $stream.Dispose() }
    Run-Check @('--verify',$cache) 2 | Out-Null
    $results.Add('PASS modified PDB rejected by SHA-256 even when its identity is intact')
    Run-Check @('--download',$manifest,$cache) | Out-Null
    Run-Check @('--verify',$cache) | Out-Null
    $results.Add('PASS corrupted cache repaired by redownload')
    $record = (Get-Content $manifest | Select-Object -First 1).Split(',')
    $wrong = Join-Path $root ('build\wrong-identity-'+(Get-Date -Format 'yyyyMMdd_HHmmss_fff'))
    $wrongKey = '000000000000000000000000000000001'
    $wrongDir = Join-Path (Join-Path $wrong $record[0]) $wrongKey
    New-Item -ItemType Directory -Path $wrongDir -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $wrongDir $record[0]),$original)
    [IO.File]::WriteAllText((Join-Path $wrong 'symbols.manifest'),($record[0]+','+$wrongKey+',1'))
    Run-Check @('--verify',$wrong) 2 | Out-Null
    $results.Add('PASS valid PDB with wrong requested GUID rejected')
    $missing = Join-Path $root 'build\nonexistent.manifest'
    [IO.File]::WriteAllText($missing,('symboltool_nonexistent_'+[Guid]::NewGuid().ToString('N')+'.pdb,'+$wrongKey+',1'))
    Run-Check @('--download',$missing,$cache) 2 | Out-Null
    $report = Get-ChildItem (Join-Path $cache 'reports') -Directory | Sort-Object Name -Descending | Select-Object -First 1
    $data = Get-Content (Join-Path $report.FullName 'report.json') -Raw | ConvertFrom-Json
    if ($data.Results[0].Attempts -ne 4 -or $data.Failed -ne 1 -or -not $data.Complete) { throw 'Real failure retry/report test failed' }
    if ((Get-Content (Join-Path $cache 'failed.manifest')).Length -eq 0) { throw 'No final failed manifest' }
    $results.Add('PASS real unavailable symbol attempted exactly 4 times and recorded in failed.manifest')
    Run-Check @('--download',(Join-Path $cache 'failed.manifest'),$cache) 2 | Out-Null
    $results.Add('PASS final failure manifest can be imported for another retry run')
}
$results | Set-Content (Join-Path $root 'build\validation-results.txt') -Encoding UTF8
$results
