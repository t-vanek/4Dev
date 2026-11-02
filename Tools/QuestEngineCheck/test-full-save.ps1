[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SchemaPath,
    [string]$ResultsDirectory = '.runtime/dotnet-exchange-20261031',
    [switch]$KeepFixture
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$schema = (Resolve-Path -LiteralPath $SchemaPath).Path
$results = [IO.Path]::GetFullPath((Join-Path $repo $ResultsDirectory))
if (-not $results.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Results must remain inside the repository.'
}
New-Item -ItemType Directory -Path $results -Force | Out-Null
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$sqlName = '4dev-full-save-sql-' + $suffix
$network = '4dev-exchange-' + $suffix
$password = 'Fixture!' + [Guid]::NewGuid().ToString('N')
$sqlEnv = Join-Path $results 'sql.env'
$testEnv = Join-Path $results 'test.env'
[IO.File]::WriteAllLines($sqlEnv, @('ACCEPT_EULA=Y', 'MSSQL_PID=Developer', "MSSQL_SA_PASSWORD=$password"))
[IO.File]::WriteAllLines($testEnv, @("FULL_SAVE_DB=Server=$sqlName;Database=TGame_gsp;User ID=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"))
$integration = @'
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_HOME=/work/dotnet NUGET_PACKAGES=/work/nuget
mkdir -p /work/Servers /work/Tools/QuestEngineCheck
for name in TMapSvr.Net TWorldSvr.Net FourDev.Mail; do
 mkdir -p /work/Servers/$name
 tar -C /source/Servers/$name --exclude='*/bin' --exclude='*/obj' --exclude='./TestResults' -cf - . | tar -C /work/Servers/$name -xf -
done
tar -C /source/Tools/QuestEngineCheck --exclude='./bin' --exclude='./obj' -cf - . | tar -C /work/Tools/QuestEngineCheck -xf -
dotnet test /work/Servers/TMapSvr.Net/TMapSvr.slnx -c Release -p:RestoreSources=/nuget-source -p:NuGetAudit=false -p:RuntimeIdentifiers=linux-x64 -p:RuntimeFrameworkVersion=10.0.13 --logger trx --results-directory /results/map-tests --verbosity quiet > /results/map-tests.txt
dotnet run --project /work/Tools/QuestEngineCheck -c Release -p:RestoreSources=/nuget-source -p:NuGetAudit=false -p:RuntimeIdentifiers=linux-x64 -p:RuntimeFrameworkVersion=10.0.13 -- --full-check exchange-full /source/Database/migrations/041_character_save_integrity.sql > /results/native-sql.txt
dotnet run --project /work/Tools/QuestEngineCheck -c Release --no-build -- --full-wolverine-check exchange-full /results/framework-schema.sql /source/Database/migrations/046_wolverine_mail.sql > /results/wolverine-sql.txt
cat /results/native-sql.txt
cat /results/wolverine-sql.txt
'@
[IO.File]::WriteAllText((Join-Path $results 'integration.sh'), $integration.Replace("`r`n", "`n"))
$createdSql = $false
$createdNetwork = $false
try {
    & docker network create $network | Out-Null
    if ($LASTEXITCODE) { throw 'Cannot create isolated SQL test network.' }
    $createdNetwork = $true
    & docker run -d --name $sqlName --network $network --env-file $sqlEnv --memory 2g mcr.microsoft.com/mssql/server:2022-latest | Out-Null
    if ($LASTEXITCODE) { throw 'Cannot start disposable SQL test instance.' }
    $createdSql = $true
    Write-Host "Disposable SQL fixture: $sqlName"
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        & docker exec $sqlName /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -Q "SELECT 1"' *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) { throw 'Disposable SQL did not become ready.' }
    & docker cp $schema "${sqlName}:/tmp/fixture.sql"
    if ($LASTEXITCODE) { throw 'Cannot copy synthetic schema into disposable SQL.' }
    & docker exec $sqlName /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -i /tmp/fixture.sql' > (Join-Path $results 'schema-load.txt')
    if ($LASTEXITCODE) { throw 'Synthetic schema preparation failed.' }
    $nuget = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
    & docker run --rm --network $network --read-only --tmpfs /work:rw,exec,size=3g --tmpfs /tmp:rw,exec,size=512m --env-file $testEnv `
        --mount "type=bind,source=$repo,target=/source,readonly" --mount "type=bind,source=$nuget,target=/nuget-source,readonly" `
        --mount "type=bind,source=$results,target=/results" mcr.microsoft.com/dotnet/sdk:10.0 bash /results/integration.sh
    if ($LASTEXITCODE) { throw 'Isolated .NET/SQL checks failed; inspect results.' }
}
finally {
    if (-not $KeepFixture) {
        if ($createdSql) { & docker rm -f $sqlName | Out-Null }
        if ($createdNetwork) { & docker network rm $network | Out-Null }
        # These exact task-owned environment files contain only disposable fixture credentials.
        Remove-Item -LiteralPath $sqlEnv, $testEnv -Force -ErrorAction SilentlyContinue
    }
}
