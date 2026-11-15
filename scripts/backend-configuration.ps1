function Get-JarvisConfiguration {
    param([string]$Path = '')
    if (!$Path) { $Path = Join-Path (Split-Path $PSScriptRoot -Parent) 'appsettings.json' }
    if (!(Test-Path -LiteralPath $path)) { throw 'Create the private root appsettings.json before starting Core.' }
    return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
}
function Set-JarvisComposeEnvironment {
    param($Configuration)
    $values = @{
        COMPOSE_DISABLE_ENV_FILE='1'; JARVIS_DATABASE=$Configuration.Database.Name; JARVIS_DATABASE_USER=$Configuration.Database.Username;
        JARVIS_POSTGRES_PASSWORD=$Configuration.Database.Password; JARVIS_POSTGRES_PORT=[string]$Configuration.Database.ExposedPort;
        JARVIS_API_PORT=[string]$Configuration.Server.HttpPort; JARVIS_TLS_PORT=[string]$Configuration.Server.HttpsPort
        JARVIS_CONTAINER_HTTP_PORT=[string]$Configuration.Server.Container.HttpPort; JARVIS_CONTAINER_TLS_PORT=[string]$Configuration.Server.Container.HttpsPort;
        JARVIS_DATABASE_PORT=[string]$Configuration.Database.Port; JARVIS_CERTIFICATE_SOURCE=[string]$Configuration.Server.Windows.CertificatePath;
        JARVIS_CERTIFICATE_TARGET=[string]$Configuration.Server.Container.CertificatePath
    }
    $previous = @{}
    foreach ($entry in $values.GetEnumerator()) { $previous[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key); [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
    return $previous
}
function Restore-JarvisComposeEnvironment {
    param($Previous)
    foreach ($entry in $Previous.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
}
