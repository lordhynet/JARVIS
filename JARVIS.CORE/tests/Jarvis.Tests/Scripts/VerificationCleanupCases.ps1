param([Parameter(Mandatory)][string]$DeploymentScript, [Parameter(Mandatory)][string]$TemporaryRoot)
$ErrorActionPreference='Stop'
$resolved=[IO.Path]::GetFullPath($TemporaryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) -cne $resolved) { throw 'Test temporary root was not isolated.' }
$global:FixtureDockerMode='empty'; $global:FixtureDockerCalls=0
function global:docker {
    if ($global:FixtureDockerMode -ceq 'rehearsal-failure') {
        $projectIndex=[Array]::IndexOf($args,'-p')
        if ($args[0] -cne 'compose' -or $projectIndex -lt 0 -or $args[$projectIndex+1] -cnotmatch '^jarvis-verify-[0-9a-f]{10}$' -or
            !(($args -contains 'up') -or ($args -contains 'down'))) { throw 'Unexpected rehearsal operation in failure-path test.' }
        $global:FailedFixtureProject=$args[$projectIndex+1]
        $global:LASTEXITCODE=19
        return
    }
    if ($args.Count -ne 4 -or $args[0] -cne 'ps' -or $args[1] -cne '-aq' -or $args[2] -cne '--filter' -or $args[3] -cnotmatch '^label=com.docker.compose.project=jarvis-verify-[0-9a-f]{10}$') {
        throw 'Unexpected Docker operation in cleanup-only test.'
    }
    $global:FixtureDockerCalls++
    $global:LASTEXITCODE=if($global:FixtureDockerMode -ceq 'failure'){17}else{0}
    if($global:FixtureDockerMode -ceq 'present'){Write-Output 'synthetic-container'}
}
function Check([bool]$Condition,[string]$Case){if(!$Condition){throw "Cleanup regression: $Case"}}
function Fixture {
    $project='jarvis-verify-'+[guid]::NewGuid().ToString('N').Substring(0,10)
    $directory=Join-Path $resolved $project
    New-Item -ItemType Directory -Path $directory|Out-Null
    foreach($name in @('appsettings.json','restored-appsettings.json')){[IO.File]::WriteAllText((Join-Path $directory $name),'synthetic-fixture-value-do-not-emit')}
    @{version=1;project=$project;kind='jarvis-verification-fixture'}|ConvertTo-Json -Compress|Set-Content (Join-Path $directory 'verification-owner.json')
    [IO.File]::WriteAllText((Join-Path $directory 'keep.dump.dpapi'),'synthetic retained backup')
    return @{project=$project;directory=$directory}
}
function Refusal([string]$Project,[string]$Case) {
    $failure=$null
    try {$null=& $DeploymentScript -CleanupProject $Project} catch {$failure=$_.Exception.Message}
    Check ($null -ne $failure) $Case
    Check (!$failure.Contains('synthetic-fixture-value-do-not-emit')) 'no credential value in errors'
    return $failure
}
$cases=[Collections.Generic.List[string]]::new()
$f=Fixture
$output=@(& $DeploymentScript -CleanupProject $f.project) -join "`n"
Check (!(Test-Path (Join-Path $f.directory 'appsettings.json'))) 'removed first settings'
Check (!(Test-Path (Join-Path $f.directory 'restored-appsettings.json'))) 'removed restored settings'
Check (Test-Path (Join-Path $f.directory 'keep.dump.dpapi')) 'preserved backup'
Check ($output.Contains($f.directory) -and !$output.Contains('synthetic-fixture-value-do-not-emit')) 'reported path only'
$null=& $DeploymentScript -CleanupProject $f.project
$cases.Add('cleanup-idempotence-and-backup-preservation')
$f=Fixture; $global:FixtureDockerMode='present'
$errorText=Refusal $f.project 'container presence refusal'
Check ($errorText.Contains('still exist') -and $errorText.Contains($f.directory)) 'container refusal retained path'
Check (Test-Path (Join-Path $f.directory 'appsettings.json')) 'container refusal preserved settings'
$cases.Add('existing-containers-refuse')
$f=Fixture; $global:FixtureDockerMode='failure'
$errorText=Refusal $f.project 'Docker inspection failure'
Check ($errorText.Contains('inspect') -and (Test-Path (Join-Path $f.directory 'restored-appsettings.json'))) 'inspection failure preserved settings'
$cases.Add('inspection-failure-refuses')
$global:FixtureDockerMode='empty'; $calls=$global:FixtureDockerCalls
$null=Refusal '../jarvis-verify-0123456789' 'traversal validation'
Check ($global:FixtureDockerCalls -eq $calls) 'invalid project never reached Docker'
$cases.Add('traversal-refuses')
$f=Fixture; Remove-Item -LiteralPath (Join-Path $f.directory 'verification-owner.json')
$errorText=Refusal $f.project 'ownership required'
Check ($errorText.Contains('ownership') -and (Test-Path (Join-Path $f.directory 'appsettings.json'))) 'missing marker preserved settings'
$cases.Add('ownership-required')
$f=Fixture
@{version=1;project='jarvis-verify-0000000000';kind='jarvis-verification-fixture'}|ConvertTo-Json -Compress|Set-Content (Join-Path $f.directory 'verification-owner.json')
$errorText=Refusal $f.project 'ownership identity match'
Check ($errorText.Contains('mismatch') -and (Test-Path (Join-Path $f.directory 'appsettings.json'))) 'wrong marker preserved settings'
$cases.Add('ownership-mismatch-refuses')
$f=Fixture
$linkProject='jarvis-verify-'+[guid]::NewGuid().ToString('N').Substring(0,10)
$link=Join-Path $resolved $linkProject
@{version=1;project=$linkProject;kind='jarvis-verification-fixture'}|ConvertTo-Json -Compress|Set-Content (Join-Path $f.directory 'verification-owner.json')
if([OperatingSystem]::IsWindows()){New-Item -ItemType Junction -Path $link -Target $f.directory|Out-Null}
else {New-Item -ItemType SymbolicLink -Path $link -Target $f.directory|Out-Null}
$errorText=Refusal $linkProject 'linked project refusal'
Check ($errorText.Contains('linked') -and (Test-Path (Join-Path $f.directory 'appsettings.json'))) 'linked target untouched'
[IO.Directory]::Delete($link)
$cases.Add('linked-directory-refuses')
$f=Fixture; $calls=$global:FixtureDockerCalls; $failure=$null
try {$null=& $DeploymentScript -CleanupProject $f.project -Port 5298} catch {$failure=$_.Exception.Message}
Check ($null -ne $failure -and $global:FixtureDockerCalls -eq $calls) 'cleanup cannot run rehearsal'
$cases.Add('cleanup-only-mode')
$global:FixtureDockerMode='rehearsal-failure'; $global:FailedFixtureProject=$null; $failure=$null
try {$null=& $DeploymentScript -Port 5298} catch {$failure=$_.Exception.Message}
Check ($null -ne $global:FailedFixtureProject) 'normal failure reached only mocked Docker'
$retained=Join-Path $resolved $global:FailedFixtureProject
Check ($null -ne $failure -and $failure.Contains($retained) -and $failure.Contains('-CleanupProject '+$global:FailedFixtureProject)) 'normal failure reported retained path and command'
Check (Test-Path (Join-Path $retained 'verification-owner.json')) 'normal failure retained ownership'
Check (Test-Path (Join-Path $retained 'appsettings.json')) 'normal failure retained generated settings'
$global:FixtureDockerMode='empty'
$null=& $DeploymentScript -CleanupProject $global:FailedFixtureProject
Check (!(Test-Path (Join-Path $retained 'appsettings.json'))) 'retained normal fixture cleaned safely'
$cases.Add('normal-failure-retention-and-cleanup')
@{passed=$cases.Count;cases=$cases;dockerMocked=$true}|ConvertTo-Json -Compress
