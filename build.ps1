$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csPath = Join-Path $root 'TurnOffScreen.cs'
$outDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$exePath = Join-Path $outDir 'TurnOffScreen.exe'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw '.NET Framework 4.x csc.exe not found.'
}

& $csc /nologo /target:winexe /optimize+ `
    /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll `
    /r:System.Management.dll `
    /out:$exePath $csPath

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exePath)) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Built successfully: $exePath ($((Get-Item $exePath).Length) bytes)"
