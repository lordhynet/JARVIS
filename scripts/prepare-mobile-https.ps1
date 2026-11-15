$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    . (Join-Path $PSScriptRoot 'backend-configuration.ps1')
    $configuration = Get-JarvisConfiguration
    $password = $configuration.Server.Tls.Password
    if (!$password) {
        $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $configuration.Server.Tls.Password = $password
        $configuration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath appsettings.json
    }
    if ($password.Length -lt 16) { throw 'The private TLS password must contain at least 16 characters.' }
    New-Item -ItemType Directory -Path .https -Force | Out-Null
    $path = $configuration.Server.Windows.CertificatePath
    if (!(Test-Path -LiteralPath $path)) {
        $rsa = [Security.Cryptography.RSA]::Create(3072)
        try {
            $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa,
                [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
            $san.AddDnsName('localhost'); $san.AddIpAddress([Net.IPAddress]::Loopback)
            $request.CertificateExtensions.Add($san.Build())
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))
            $usages = [Security.Cryptography.OidCollection]::new()
            [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $true))
            $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(1))
            [IO.File]::WriteAllBytes($path, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
        } finally { $rsa.Dispose() }
    } else { $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($path, $password,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet) }
    if ($cert.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) { throw 'The local HTTPS certificate expired. Rotate it and pair the phone again.' }
    [IO.File]::WriteAllText((Join-Path (Get-Location) '.https/server.crt'), $cert.ExportCertificatePem())
    @{ url=$configuration.Server.AndroidBridgeUrl; certificateSha256=$cert.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256).ToLowerInvariant(); expiresAt=$cert.NotAfter.ToUniversalTime() } |
        ConvertTo-Json | Set-Content -LiteralPath .https/server-info.json
    Write-Host 'Local HTTPS certificate prepared. No OS trust store was changed.'
} finally { Pop-Location }
