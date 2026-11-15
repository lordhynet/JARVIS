param([string]$OutputPath = (Join-Path $PSScriptRoot '../TestResults/jarvis-sbom.spdx.json'), [string]$Image = '')
$ErrorActionPreference = 'Stop'
$core = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path (Split-Path $core -Parent) '.tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
Push-Location $core
try {
    $packageJson = & $sdk list Jarvis.sln package --include-transitive --format json --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect restored package inventory.' }
    $inventory = ($packageJson -join [Environment]::NewLine) | ConvertFrom-Json
    $seen = @{}; $packages = [Collections.Generic.List[object]]::new()
    foreach ($project in $inventory.projects) {
        if ($project.path -match '[/\\](tests|tools)[/\\]') { continue }
        foreach ($framework in $project.frameworks) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                if (!$package) { continue }; $identity = $package.id + '@' + $package.resolvedVersion
                if ($seen.ContainsKey($identity)) { continue }; $seen[$identity] = $true
                $packages.Add(@{ SPDXID = 'SPDXRef-NuGet-' + ($identity -replace '[^A-Za-z0-9.-]', '-'); name=$package.id; versionInfo=$package.resolvedVersion;
                    downloadLocation='NOASSERTION'; filesAnalyzed=$false; licenseConcluded='NOASSERTION'; licenseDeclared='NOASSERTION';
                    externalRefs=@(@{referenceCategory='PACKAGE-MANAGER';referenceType='purl';referenceLocator=('pkg:nuget/' + $package.id + '@' + $package.resolvedVersion)}) })
            }
        }
    }
    $imageEvidence = $null
    if ($Image) {
        $imageEvidence = (& docker image inspect $Image --format '{{.Id}}') -join ''
        if ($LASTEXITCODE -ne 0) { throw 'Requested image is not available locally.' }
        $osPackages = & docker run --rm --network none --entrypoint dpkg-query $Image -W '--showformat=${Package}\t${Version}\n'
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect container OS packages.' }
        foreach ($line in $osPackages) {
            $parts = $line -split "`t", 2
            if ($parts.Count -eq 2) { $packages.Add(@{SPDXID='SPDXRef-OS-' + ($parts[0] -replace '[^A-Za-z0-9.-]', '-');name=$parts[0];versionInfo=$parts[1];downloadLocation='NOASSERTION';filesAnalyzed=$false;licenseConcluded='NOASSERTION';licenseDeclared='NOASSERTION'}) }
        }
    }
    $document = @{spdxVersion='SPDX-2.3';dataLicense='CC0-1.0';SPDXID='SPDXRef-DOCUMENT';name='JARVIS dependency inventory';
        documentNamespace=('https://jarvis.local/sbom/' + [guid]::NewGuid());creationInfo=@{created=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ');creators=@('Tool: JARVIS-SBOM-1')};
        packages=$packages.ToArray();comment=('Generated from restored production project dependencies. Container evidence: ' + $imageEvidence + '. NOASSERTION is not a license or vulnerability clearance.')}
    $target = [IO.Path]::GetFullPath($OutputPath); New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    $document | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $target -Encoding utf8
    Write-Host "SBOM inventory saved ($($packages.Count) packages). This is not a CVE scan."
} finally { Pop-Location }
