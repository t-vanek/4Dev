# Creates private, reusable TLS identities and Docker configuration for one-PC development.
# Requires PowerShell 7. Use docker compose up -d --build after running this script.
# Existing database volumes, passwords and valid certificates are preserved.
#Requires -Version 7.4
param(
    [string]$BackupDirectory = "",
    [string]$ClientAddress = "127.0.0.1"
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $repo '.runtime'
$envFile = Join-Path $repo '.env'
$utf8 = [Text.UTF8Encoding]::new($false)
$address = $null
if (-not [Net.IPAddress]::TryParse($ClientAddress, [ref]$address) -or
    $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'ClientAddress must be an IPv4 address.'
}

New-Item -ItemType Directory -Path $runtime -Force | Out-Null
if ($IsWindows) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagate = [Security.AccessControl.PropagationFlags]::None
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, $propagate, 'Allow'))
    }
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($runtime), $acl)
} else { & chmod 700 $runtime; if ($LASTEXITCODE) { throw 'Cannot protect runtime directory.' } }

# Only simple KEY=value or KEY='value' entries; never execute the contents of .env.
$settings = @{}
if (Test-Path -LiteralPath $envFile) {
    foreach ($line in [IO.File]::ReadAllLines($envFile)) {
        if ($line -match '^\s*([A-Z][A-Z0-9_]*)=(.*)$') {
            $value = $Matches[2].Trim()
            if ($value.StartsWith("'")) {
                if (-not $value.EndsWith("'")) { throw 'Unterminated value in .env.' }
                $value = $value.Substring(1, $value.Length - 2)
            } elseif ($value.StartsWith('"') -or $value.Contains('$') -or $value.Contains(' #')) {
                throw 'Use single-quoted literal values in .env.'
            }
            $settings[$Matches[1]] = $value
        }
    }
}
function Set-LocalSetting([string]$Name, [string]$Value) {
    if ($Value -match "['\r\n]") { throw "Unsupported characters in $Name." }
    $settings[$Name] = $Value
    $lines = if (Test-Path -LiteralPath $envFile) { @([IO.File]::ReadAllLines($envFile)) } else { @() }
    $lines = @($lines | Where-Object { $_ -notmatch "^\s*$Name=" }) + "$Name='$Value'"
    [IO.File]::WriteAllLines($envFile, [string[]]$lines, $utf8)
}
if (-not $settings.ContainsKey('SA_PASSWORD')) {
    # A new install only. Existing SQL volumes require their original password in .env.
    Set-LocalSetting 'SA_PASSWORD' ('Local1!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)))
}
if ($settings.SA_PASSWORD.Length -lt 8 -or $settings.SA_PASSWORD -match "[';\r\n]") {
    throw 'SA_PASSWORD must have at least 8 characters and cannot contain a quote, semicolon or newline for the local importer.'
}
if (-not $settings.ContainsKey('PEER_CERT_PASSWORD')) {
    Set-LocalSetting 'PEER_CERT_PASSWORD' ([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
}
if ($BackupDirectory) {
    Set-LocalSetting 'BACKUP_DIRECTORY' ((Resolve-Path -LiteralPath $BackupDirectory).Path.Replace('\', '/'))
} elseif (-not $settings.ContainsKey('BACKUP_DIRECTORY')) {
    Set-LocalSetting 'BACKUP_DIRECTORY' $repo.Replace('\', '/')
}
foreach ($file in 'TGLOBAL_RAGEZONE.bak', 'TGAME_RAGEZONE.bak') {
    if (-not (Test-Path -LiteralPath (Join-Path $settings.BACKUP_DIRECTORY $file) -PathType Leaf)) {
        throw "Missing $file in BACKUP_DIRECTORY. Pass -BackupDirectory with the baseline backup folder."
    }
}

function Get-LocalIdentity([string]$Name, [string[]]$Usage, [bool]$IsCa = $false) {
    $directory = Join-Path $runtime "certs/$Name"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $path = Join-Path $directory 'identity.pfx'
    if (Test-Path -LiteralPath $path) {
        $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($path,
            $settings.PEER_CERT_PASSWORD, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]'EphemeralKeySet, Exportable')
        if (-not $cert.HasPrivateKey -or $cert.NotAfter.ToUniversalTime() -lt [DateTime]::UtcNow.AddDays(7)) {
            $cert.Dispose()
            throw "The $Name certificate needs explicit renewal; preserve the old files before replacing them."
        }
    } else {
        $key = [Security.Cryptography.RSA]::Create(3072)
        try {
            $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=$Name", $key,
                [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
            $san.AddDnsName($Name); $san.AddDnsName('localhost'); $san.AddIpAddress([Net.IPAddress]::Loopback)
            $request.CertificateExtensions.Add($san.Build())
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($IsCa, $false, 0, $true))
            $flags = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]'DigitalSignature, KeyEncipherment'
            if ($IsCa) { $flags = $flags -bor [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign }
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($flags, $true))
            $oids = [Security.Cryptography.OidCollection]::new()
            foreach ($oid in $Usage) { $oids.Add([Security.Cryptography.Oid]::new($oid)) | Out-Null }
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $true))
            $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(365))
            [IO.File]::WriteAllBytes($path, $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $settings.PEER_CERT_PASSWORD))
        } finally { $key.Dispose() }
    }
    [IO.File]::WriteAllText((Join-Path $directory 'cert.pem'), $cert.ExportCertificatePem(), $utf8)
    if ($Name -eq 'mailpit') {
        $privateKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
        try { [IO.File]::WriteAllText((Join-Path $directory 'key.pem'), $privateKey.ExportPkcs8PrivateKeyPem(), $utf8) }
        finally { $privateKey.Dispose() }
    }
    return $cert
}
$world = Get-LocalIdentity 'worldsvr' @('1.3.6.1.5.5.7.3.1')
$map = Get-LocalIdentity 'mapsvr' @('1.3.6.1.5.5.7.3.1', '1.3.6.1.5.5.7.3.2')
$control = Get-LocalIdentity 'controlsvr' @('1.3.6.1.5.5.7.3.2')
# This local SMTP trust anchor is scoped to the Login container, not the host trust store.
$mail = Get-LocalIdentity 'mailpit' @('1.3.6.1.5.5.7.3.1') $true
try {
    $hash = [Security.Cryptography.HashAlgorithmName]::SHA256
    $worldPin = $world.GetCertHashString($hash); $mapPin = $map.GetCertHashString($hash); $controlPin = $control.GetCertHashString($hash)
    function Write-LocalConfig([string]$Name, $Value) {
        $directory = Join-Path $runtime "config/$Name"
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $directory 'appsettings.LocalDocker.json'), ($Value | ConvertTo-Json -Depth 12), $utf8)
    }
    function Client-Tls([string]$Name, [string]$Pin) {
        return @{ CertificatePath = '/run/certs/identity.pfx'; CertificatePassword = $settings.PEER_CERT_PASSWORD; ServerName = $Name; ServerSha256 = $Pin }
    }
    $controlPeer = @{ Sha256 = $controlPin; Role = 'Control'; ServerId = 0 }
    Write-LocalConfig 'world' @{ World = @{ PeerTls = @{
        CertificatePath = '/run/certs/identity.pfx'; CertificatePassword = $settings.PEER_CERT_PASSWORD
        Peers = @(@{ Sha256 = $mapPin; Role = 'Map'; ServerId = 1025 }, $controlPeer)
    } } }
    Write-LocalConfig 'map' @{ Map = @{
        WorldTls = (Client-Tls 'worldsvr' $worldPin); ControlPort = 5817
        ControlTls = @{ CertificatePath = '/run/certs/identity.pfx'; CertificatePassword = $settings.PEER_CERT_PASSWORD; Peers = @($controlPeer) }
    } }
    Write-LocalConfig 'control' @{ Control = @{ Servers = @(
        @{ Host = 'worldsvr'; Port = 3816; Group = 1; Type = 3; ServerId = 1; Name = 'TWORLD_GSP'; PeerTls = (Client-Tls 'worldsvr' $worldPin) },
        @{ Host = 'mapsvr'; Port = 5817; Group = 1; Type = 4; ServerId = 1; Name = 'TMAP_GSP'; PeerTls = (Client-Tls 'mapsvr' $mapPin) }
    ) } }
    Write-LocalConfig 'login' @{ Login = @{ TwoFactor = @{ Smtp = @{
        Host = 'mailpit'; Port = 1025; Encryption = 'SslOnConnect'; From = 'login@4dev.local'; UserName = ''; Password = ''
    } } } }
    # Applied after migrations on every importer run, so restored and existing databases agree.
    [IO.File]::WriteAllText((Join-Path $runtime 'configure-topology.sql'), @"
USE [TGlobal_gsp];
UPDATE dbo.TIPADDR SET szIPAddr = '$ClientAddress';
GO
"@, $utf8)
} finally { $world.Dispose(); $map.Dispose(); $control.Dispose(); $mail.Dispose() }
if (-not $IsWindows) { & chmod -R go-rwx $runtime }
Write-Host 'Local configuration ready in .runtime; secrets remain in ignored files.'
Write-Host "Client endpoint: ${ClientAddress}:4816. Mailpit: http://127.0.0.1:8025"
