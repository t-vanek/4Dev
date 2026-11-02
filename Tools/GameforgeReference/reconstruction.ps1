param(
    [ValidateSet('Start','Stop','Collect','ImportClient','ImportPublisher','Status','Report','Entity')]
    [string]$Action='Status',
    [string]$Kind,
    [string]$Entity,
    [int]$Interval=30
)
$ErrorActionPreference='Stop'
$taskRepo=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$taskWork=Join-Path $taskRepo '.runtime\reconstruction'
$taskScript=Join-Path $PSScriptRoot 'collect_reconstruction.py'
$taskPython=(Get-Command python -ErrorAction Stop).Source
New-Item -ItemType Directory -Path $taskWork -Force | Out-Null
Push-Location $taskRepo
try {
    switch($Action) {
        'Start' {
            $taskPidFile=Join-Path $taskWork 'watcher.pid'
            if(Test-Path -LiteralPath $taskPidFile){
                $taskPreviousPid=[int](Get-Content -LiteralPath $taskPidFile -Raw).Trim()
                if(Get-Process -Id $taskPreviousPid -ErrorAction SilentlyContinue){throw ('Collector is already running: '+$taskPreviousPid)}
            }
            $taskStop=Join-Path $taskWork 'STOP'
            if(Test-Path -LiteralPath $taskStop){Remove-Item -LiteralPath $taskStop}
            $taskArgs='-u "'+$taskScript+'" watch --interval '+$Interval
            $taskProcess=Start-Process -FilePath $taskPython -ArgumentList $taskArgs -WorkingDirectory $taskRepo -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskWork 'watch.log') -RedirectStandardError (Join-Path $taskWork 'watch.err')
            Write-Output ('Collector process requested: '+$taskProcess.Id)
        }
        'Stop' {Set-Content -LiteralPath (Join-Path $taskWork 'STOP') -Value 'User requested collector stop';Write-Output 'Collector will stop after the current SQL batch; Wireshark is independent.'}
        'Collect' {& $taskPython $taskScript capture-once}
        'ImportClient' {& $taskPython $taskScript import-client}
        'ImportPublisher' {& $taskPython $taskScript import-publisher}
        'Status' {& $taskPython $taskScript status; if(Test-Path -LiteralPath (Join-Path $taskWork 'last-cycle.json')){Get-Content -LiteralPath (Join-Path $taskWork 'last-cycle.json')}}
        'Report' {& $taskPython $taskScript export}
        'Entity' {if(-not $Kind -or -not $Entity){throw 'Entity requires -Kind and -Entity'};& $taskPython $taskScript entity --kind $Kind --entity $Entity}
    }
    if($Action -notin @('Start','Stop') -and $LASTEXITCODE){throw ('Collector command failed: '+$LASTEXITCODE)}
} finally {Pop-Location}
