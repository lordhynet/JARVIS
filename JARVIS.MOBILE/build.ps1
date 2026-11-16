param(
    [string]$SdkRoot = (Join-Path $PSScriptRoot '../.tools/android-sdk'),
    [string]$Gradle = (Join-Path $PSScriptRoot '../.tools/gradle-8.11.1/bin/gradle.bat'),
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'
$sdk = (Resolve-Path -LiteralPath $SdkRoot).Path
$oldGradleHome = $env:GRADLE_USER_HOME
$oldAndroidUserHome = $env:ANDROID_USER_HOME
$oldSigningPassword = $env:JARVIS_SIGN_PASSWORD
Push-Location $PSScriptRoot
try {
    $env:GRADLE_USER_HOME = Join-Path $PSScriptRoot '../.tools/gradle-cache'
    $env:ANDROID_USER_HOME = Join-Path $PSScriptRoot '../.tools/android-user'
    New-Item -ItemType Directory -Path $env:ANDROID_USER_HOME -Force | Out-Null
    Set-Content -LiteralPath local.properties -Value ('sdk.dir=' + $sdk.Replace('\','/'))
    New-Item -ItemType Directory -Path .signing -Force | Out-Null
    if (!(Test-Path -LiteralPath .signing/signing.properties)) {
        $env:JARVIS_SIGN_PASSWORD = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
        & (Join-Path $env:JAVA_HOME 'bin/keytool.exe') -genkeypair -keystore .signing/jarvis-test.jks -storepass:env JARVIS_SIGN_PASSWORD -keypass:env JARVIS_SIGN_PASSWORD -alias jarvis -keyalg RSA -keysize 3072 -validity 3650 -dname 'CN=JARVIS Personal Test, OU=Personal, O=JARVIS, C=GB'
        if ($LASTEXITCODE -ne 0) { throw 'APK signing key generation failed.' }
        Set-Content -LiteralPath .signing/signing.properties -Value ('storePassword=' + $env:JARVIS_SIGN_PASSWORD)
    }
    $tasks = @(':app:assembleRelease', ':app:testReleaseUnitTest', ':app:lintRelease', '--no-daemon')
    if ($Clean) { $tasks = @('clean') + $tasks }
    & $Gradle @tasks
    if ($LASTEXITCODE -ne 0) { throw 'Android build or verification failed.' }
    $artifacts = Join-Path $PSScriptRoot '../artifacts'
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $apk = Join-Path $artifacts 'JARVIS-Android-0.10.2.apk'
    Copy-Item -LiteralPath app/build/outputs/apk/release/app-release.apk -Destination $apk -Force
    & (Join-Path $sdk 'build-tools/35.0.0/apksigner.bat') verify --verbose $apk
    if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
    Get-FileHash -LiteralPath $apk -Algorithm SHA256
} finally {
    $env:GRADLE_USER_HOME = $oldGradleHome
    $env:ANDROID_USER_HOME = $oldAndroidUserHome
    $env:JARVIS_SIGN_PASSWORD = $oldSigningPassword
    Pop-Location
}
