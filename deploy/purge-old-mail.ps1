<#
.SYNOPSIS
    Deletes captured .eml files older than a given age, locally and/or from the Azure Files share.

.DESCRIPTION
    The sink never deletes anything by itself, so this handles retention. Point it at a local
    folder, an Azure Files share, or both. Empty date folders are removed once their contents go.

    Use -Install to register a Windows scheduled task that runs this script every hour with the
    same target arguments, which is what makes the deletion automatic.

    The storage key is never stored and never reaches a command line: it is fetched from az at run
    time into AZURE_STORAGE_KEY, which the az storage commands read from the environment, and
    cleared again afterwards.

.PARAMETER OlderThanHours
    Age threshold. Files last modified before now minus this many hours are deleted. Default 24.

.EXAMPLE
    ./purge-old-mail.ps1 -Path C:\Git\mail-sink\src\MailSink\mail -WhatIf

.EXAMPLE
    ./purge-old-mail.ps1 -ResourceGroup rg-mailsink -StorageAccount <storage>

.EXAMPLE
    ./purge-old-mail.ps1 -ResourceGroup rg-mailsink -StorageAccount <storage> -Install
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # 0 deletes everything currently stored.
    [ValidateRange(0, 8760)]
    [int]$OlderThanHours = 24,

    # Local target.
    [string]$Path,

    # Azure Files target. Both must be supplied together.
    [string]$ResourceGroup,
    [string]$StorageAccount,
    [string]$ShareName = 'mail',
    [string]$Subscription,

    # Register a scheduled task instead of purging now.
    [switch]$Install,
    [string]$TaskName = 'mail-sink purge',
    [ValidateRange(1, 24)]
    [int]$RunEveryHours = 1
)

$ErrorActionPreference = 'Stop'

if (-not $Path -and -not $StorageAccount) {
    throw 'Specify -Path, or -ResourceGroup with -StorageAccount, or both.'
}

if ($StorageAccount -and -not $ResourceGroup) {
    throw '-StorageAccount also needs -ResourceGroup.'
}

function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $result = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "az $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
    return $result
}

# ---------------------------------------------------------------- install mode

if ($Install) {
    $taskArgs = @('-NoProfile', '-NonInteractive', '-File', "`"$PSCommandPath`"", '-OlderThanHours', $OlderThanHours)
    if ($Path) { $taskArgs += @('-Path', "`"$Path`"") }
    if ($StorageAccount) {
        $taskArgs += @('-ResourceGroup', $ResourceGroup, '-StorageAccount', $StorageAccount, '-ShareName', $ShareName)
    }
    if ($Subscription) { $taskArgs += @('-Subscription', $Subscription) }

    $pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue)?.Source ?? (Get-Command powershell).Source
    $action = New-ScheduledTaskAction -Execute $pwsh -Argument ($taskArgs -join ' ')
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) `
        -RepetitionInterval (New-TimeSpan -Hours $RunEveryHours)
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries `
        -AllowStartIfOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 30)

    if ($PSCmdlet.ShouldProcess($TaskName, "Register scheduled task running every $RunEveryHours h")) {
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
            -Settings $settings -Description "Deletes mail sink .eml files older than $OlderThanHours hours." -Force | Out-Null
        Write-Host "Registered scheduled task '$TaskName', running every $RunEveryHours h." -ForegroundColor Green
        Write-Host "  Remove it with: Unregister-ScheduledTask -TaskName '$TaskName' -Confirm:`$false"
    }
    return
}

# ---------------------------------------------------------------- purge mode

$cutoffLocal = (Get-Date).AddHours(-$OlderThanHours)
$cutoffUtc = (Get-Date).ToUniversalTime().AddHours(-$OlderThanHours)
Write-Host "Deleting .eml files last modified before $($cutoffLocal.ToString('yyyy-MM-dd HH:mm')) local time."

$deleted = 0

if ($Path) {
    Write-Host ''
    Write-Host "==> Local: $Path"

    if (-not (Test-Path $Path)) {
        Write-Warning "  '$Path' does not exist, skipping."
    }
    else {
        $stale = Get-ChildItem -Path $Path -Filter *.eml -File -Recurse |
            Where-Object { $_.LastWriteTime -lt $cutoffLocal }

        foreach ($file in $stale) {
            if ($PSCmdlet.ShouldProcess($file.FullName, 'Delete')) {
                Remove-Item -LiteralPath $file.FullName -Force
                $deleted++
            }
        }

        Write-Host "  $($stale.Count) file(s) matched."

        # Drop date folders that are now empty.
        Get-ChildItem -Path $Path -Directory |
            Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
            ForEach-Object {
                if ($PSCmdlet.ShouldProcess($_.FullName, 'Remove empty folder')) {
                    Remove-Item -LiteralPath $_.FullName -Force
                }
            }
    }
}

if ($StorageAccount) {
    Write-Host ''
    Write-Host "==> Azure Files: $StorageAccount/$ShareName"

    if ($Subscription) {
        Invoke-Az @('account', 'set', '--subscription', $Subscription) | Out-Null
    }

    # az storage reads the account and key from the environment, so neither is ever passed as an
    # argument, where any other local user could read it out of the process command line. This
    # runs unattended every hour under -Install, so that exposure would be continuous.
    $env:AZURE_STORAGE_ACCOUNT = $StorageAccount
    $env:AZURE_STORAGE_KEY = Invoke-Az @('storage', 'account', 'keys', 'list', '-g', $ResourceGroup,
        '--account-name', $StorageAccount, '--query', '[0].value', '-o', 'tsv')

    try {
        $base = @('storage', 'file', 'list', '--share-name', $ShareName, '-o', 'json')

        function Get-ShareEntries {
            param([string]$Directory)
            $arguments = $base
            if ($Directory) { $arguments += @('--path', $Directory) }
            return (Invoke-Az $arguments | ConvertFrom-Json)
        }

        $matched = 0
        $root = Get-ShareEntries
        # The share is either flat, or one level of yyyy-MM-dd folders (MailSink:GroupByDate).
        $directories = @('') + @($root | Where-Object { $_.type -eq 'dir' } | ForEach-Object { $_.name })

        foreach ($directory in $directories) {
            $entries = if ($directory) { Get-ShareEntries -Directory $directory } else { $root }

            foreach ($entry in @($entries | Where-Object { $_.type -eq 'file' -and $_.name -like '*.eml' })) {
                # ConvertFrom-Json already yields a DateTime; re-parsing its string form breaks under
                # non-US locales, so only parse when az handed back a plain string.
                $raw = $entry.properties.lastModified
                $lastModified = if ($raw -is [datetime]) {
                    $raw
                }
                else {
                    [datetime]::Parse($raw, [cultureinfo]::InvariantCulture,
                        [System.Globalization.DateTimeStyles]::RoundtripKind)
                }

                if ($lastModified.ToUniversalTime() -ge $cutoffUtc) { continue }

                $matched++
                $filePath = if ($directory) { "$directory/$($entry.name)" } else { $entry.name }

                if ($PSCmdlet.ShouldProcess("$ShareName/$filePath", 'Delete')) {
                    Invoke-Az @('storage', 'file', 'delete', '--share-name', $ShareName, '--path', $filePath) | Out-Null
                    $deleted++
                }
            }

            # Drop the date folder once its last file is gone.
            if ($directory -and -not (Get-ShareEntries -Directory $directory)) {
                if ($PSCmdlet.ShouldProcess("$ShareName/$directory", 'Remove empty folder')) {
                    Invoke-Az @('storage', 'directory', 'delete', '--share-name', $ShareName, '--name', $directory) | Out-Null
                }
            }
        }

        Write-Host "  $matched file(s) matched."
    }
    finally {
        # Only matters when the script is dot-sourced into a live session; the variables would
        # otherwise die with the process.
        $env:AZURE_STORAGE_KEY = $null
        $env:AZURE_STORAGE_ACCOUNT = $null
    }
}

Write-Host ''
Write-Host "Deleted $deleted file(s)." -ForegroundColor Green
