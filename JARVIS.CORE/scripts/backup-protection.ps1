# Windows user-bound encryption. Existing plaintext dumps remain readable by the restore script.
function Protect-JarvisBackup([string]$Source, [string]$Target) {
    if (![Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        throw 'Encrypted backups currently require Windows DPAPI. No plaintext backup will be emitted.'
    }
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    if ((Get-Item -LiteralPath $Source).Length -gt 134217728) { throw 'This DPAPI backup path is limited to 128 MB. Use an approved streaming encrypted backup process for larger databases.' }
    $plainBackup = [IO.File]::ReadAllBytes($Source)
    try {
        $entropy = [Text.Encoding]::UTF8.GetBytes('jarvis-backup-v1')
        $protectedBackup = [Security.Cryptography.ProtectedData]::Protect($plainBackup, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $prefix = [Text.Encoding]::ASCII.GetBytes('JARVIS-DPAPI-V1:')
        $result = [byte[]]::new($prefix.Length + $protectedBackup.Length)
        [Array]::Copy($prefix, 0, $result, 0, $prefix.Length); [Array]::Copy($protectedBackup, 0, $result, $prefix.Length, $protectedBackup.Length)
        [IO.File]::WriteAllBytes($Target, $result)
        $backupSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        & icacls $Target /inheritance:r /grant:r "*$($backupSid):(F)" '*S-1-5-18:(F)' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not restrict encrypted backup permissions.' }
    } finally { [Array]::Clear($plainBackup, 0, $plainBackup.Length) }
}
function Unprotect-JarvisBackup([string]$Source, [string]$Target) {
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    $bytes = [IO.File]::ReadAllBytes($Source); $prefix = [Text.Encoding]::ASCII.GetBytes('JARVIS-DPAPI-V1:')
    if ($bytes.Length -le $prefix.Length -or [Text.Encoding]::ASCII.GetString($bytes, 0, $prefix.Length) -ne 'JARVIS-DPAPI-V1:') { throw 'Invalid encrypted backup header.' }
    $encrypted = [byte[]]::new($bytes.Length - $prefix.Length); [Array]::Copy($bytes, $prefix.Length, $encrypted, 0, $encrypted.Length)
    $plain = [Security.Cryptography.ProtectedData]::Unprotect($encrypted, [Text.Encoding]::UTF8.GetBytes('jarvis-backup-v1'), [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try { [IO.File]::WriteAllBytes($Target, $plain) } finally { [Array]::Clear($plain, 0, $plain.Length) }
}
