[CmdletBinding()]
param([string]$MakeAppx)
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path $PSScriptRoot -Parent
if (!$MakeAppx) {
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $MakeAppx = Get-ChildItem -LiteralPath $sdk -Directory | Where-Object Name -Match '^10\.0\.' |
        Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64/makeappx.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$MakeAppx -or !(Test-Path -LiteralPath $MakeAppx)) { throw 'Install the Windows SDK packaging tools (MakeAppx).' }
# Each build has its own staging directory. No recursive deletion or installation.
$artifacts = Join-Path $windowsRoot 'artifacts'
$stage = Join-Path $artifacts ('package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Push-Location $windowsRoot
try {
    & dotnet publish src/Jarvis.Windows/Jarvis.Windows.csproj -c Release -r win-x64 --self-contained true -o $stage -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Copy-Item -LiteralPath (Join-Path $windowsRoot 'package/AppxManifest.xml') -Destination $stage
    $assets = Join-Path $stage 'Assets'; New-Item -ItemType Directory -Path $assets | Out-Null
    Add-Type -AssemblyName System.Drawing
    foreach ($asset in @(@('StoreLogo', 50), @('Square44x44Logo', 44), @('Square150x150Logo', 150))) {
        $size = [int]$asset[1]; $bitmap = [Drawing.Bitmap]::new($size, $size); $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::FromArgb(21, 35, 49))
            $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(99, 224, 216), [Math]::Max(2, $size / 18))
            try { $graphics.DrawEllipse($pen, [int]($size * .2), [int]($size * .2), [int]($size * .6), [int]($size * .6)) } finally { $pen.Dispose() }
            $bitmap.Save((Join-Path $assets ($asset[0] + '.png')), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
    $package = Join-Path $artifacts 'Jarvis.Windows_0.3.0.0_x64_unsigned.msix'
    $packLog = Join-Path $artifacts 'makeappx.log'
    & $MakeAppx pack /d $stage /p $package /o *> $packLog
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $packLog -Tail 12; throw 'MSIX packaging or manifest validation failed.' }
    Get-FileHash -Algorithm SHA256 -LiteralPath $package | Format-List
    Write-Host 'Created unsigned review artifact. This script does not sign, install, launch, or enable startup.'
} finally { Pop-Location }
