<#
.SYNOPSIS
    Deploys mail sink to Azure Container Instances with an Azure Files share for the .eml output.

.DESCRIPTION
    Creates (idempotently) a container registry, a storage account with a "mail" file share, a
    key vault holding the SMTP credentials, a user-assigned managed identity, and the container
    group itself.

    Resource names are derived from -NamePrefix plus a hash of the subscription and resource group,
    so re-running the script targets the same resources instead of creating new ones.

    The SMTP username and password live in the key vault. The container is given the vault URI --
    "@Microsoft.KeyVault(SecretUri=...)" -- not the secret, and reads it at startup through its
    managed identity, so neither credential appears on a command line or in the ARM definition of
    the container group.

    No secret reaches a command line. The generated SMTP password goes to "az keyvault secret set"
    through a temporary file, because --value would put it on one. The storage account key and the
    registry password travel the same way: the container group is deployed from a generated JSON
    file rather than from switches, and that file is deleted as soon as the call returns.

    Deploying from a file is also the only way to declare a liveness probe -- ACI offers no CLI
    switch for one -- so the container group is restarted when the sink stops answering on its
    health endpoint rather than being left wedged.

.PARAMETER Exposure
    Private (default) puts the container group in a VNet with no public IP. Only reachable from
    that VNet - peered networks, VPN, or other Azure resources in it.

    Public gives it a public IP and an <dns-label>.<region>.azurecontainer.io FQDN. Every session
    has to authenticate over TLS either way, but the sink still accepts any recipient, so a public
    endpoint will be found by scanners and probed. Prefer Private unless you need otherwise.

    The certificate is issued for that FQDN when Public, so senders can verify it by name. Pass
    -CertificateSubject to issue it for the name your senders will actually use instead.

.PARAMETER SmtpUsername
    Username the sink will require. Stored in the key vault alongside the password.

.PARAMETER SmtpPassword
    Password the sink will require. Omit it and a 32-character random one is generated on the
    first run and kept in the key vault; later runs reuse it. The script prints the command to
    read it back, rather than the password itself.

.PARAMETER RotatePassword
    Replace the stored password with a freshly generated one, even though a secret already exists.

.PARAMETER CertificateSubject
    Host name the TLS certificate is issued for. Defaults to the container group's public FQDN for
    -Exposure Public, and to <name-prefix>.internal for Private, where there is no name to use.
    Set it to whatever senders will put in their SMTP host setting.

    The certificate is self-signed, because nothing else can be issued without a public DNS zone.
    Senders must therefore trust it explicitly. Replace it in the key vault with one from your own
    CA to avoid that, and the sink will pick the replacement up on its next restart.

.PARAMETER UseAdminCredentials
    Pull the image with the registry's admin username/password instead of the managed identity.
    Microsoft's docs list a Premium registry as a prerequisite for managed-identity pulls; if the
    deployment fails on the image pull against a Basic registry, re-run with this switch.

.EXAMPLE
    ./deploy.ps1

.EXAMPLE
    ./deploy.ps1 -Exposure Public -DnsLabel mailsink-demo

.EXAMPLE
    ./deploy.ps1 -ResourceGroup rg-other -Location northeurope
#>
[CmdletBinding()]
param(
    # Default to your own target without putting it in the repo: set MAILSINK_RESOURCE_GROUP and
    # MAILSINK_LOCATION in your profile, or pass these explicitly.
    [string]$ResourceGroup = ($env:MAILSINK_RESOURCE_GROUP ?? 'rg-mailsink'),
    [string]$Location = ($env:MAILSINK_LOCATION ?? 'westeurope'),

    [string]$Subscription,
    [string]$NamePrefix = 'mailsink',

    [ValidateSet('Private', 'Public')]
    [string]$Exposure = 'Private',

    # Private only. Created with the required ACI subnet delegation if they do not exist.
    [string]$VNetName = 'vnet-mailsink',
    [string]$SubnetName = 'snet-aci',
    [string]$VNetAddressPrefix = '10.30.0.0/16',
    [string]$SubnetAddressPrefix = '10.30.1.0/24',

    # Public only. Must be globally unique within the region.
    [string]$DnsLabel,

    # The standard submission ports. Privileged, but a container runtime sets
    # net.ipv4.ip_unprivileged_port_start=0, so the non-root user in the image binds them fine.
    # Move them high if a runtime ever refuses.
    [int]$StartTlsPort = 587,
    [int]$ImplicitTlsPort = 465,

    # Not published. The liveness probe reaches it inside the container group; a sender cannot.
    [int]$HealthPort = 8080,

    [string]$TimeZone = 'Europe/Amsterdam',
    [string]$Cpu = '0.5',
    [string]$Memory = '1',
    [switch]$UseAdminCredentials,

    # SMTP credentials, kept in the key vault. -SmtpPassword is generated when omitted.
    [ValidateNotNullOrEmpty()]
    [string]$SmtpUsername = 'mailsink',
    [string]$SmtpPassword,
    [switch]$RotatePassword,

    # Empty means "derive from -Exposure": the public FQDN, or <prefix>.internal when private.
    [string]$CertificateSubject,

    # Rebuild even when an image tagged with the current source hash already exists.
    [switch]$ForceBuild,

    # Recreate the container group even when it already runs this image and configuration.
    [switch]$ForceRecreate,

    # Skip the confirmation shown when deploying to the default target. Needed for unattended runs,
    # which would otherwise block on the prompt.
    [switch]$Force,

    # Private only. Locks the storage account down to the container subnet, so the captured mail
    # stops being reachable from any network with the account key. Add your own IP afterwards to
    # keep reading the share; the script prints the command.
    [switch]$RestrictStorageNetwork
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $result = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "az $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
    return $result
}

# Per-step timings, so it is obvious where a slow deployment actually spends its time rather than
# guessing. Printed as a summary at the end.
$script:StepWatch = [System.Diagnostics.Stopwatch]::StartNew()
$script:StepName = $null
$script:StepTimings = [System.Collections.Generic.List[object]]::new()

function Step {
    param([Parameter(Mandatory)][string]$Name)

    if ($script:StepName) {
        $script:StepTimings.Add([pscustomobject]@{
            Step    = $script:StepName
            Seconds = [math]::Round($script:StepWatch.Elapsed.TotalSeconds, 1)
        })
    }

    $script:StepName = $Name
    $script:StepWatch.Restart()
    Write-Host "==> $Name"
}

function Stop-Steps {
    if ($script:StepName) {
        $script:StepTimings.Add([pscustomobject]@{
            Step    = $script:StepName
            Seconds = [math]::Round($script:StepWatch.Elapsed.TotalSeconds, 1)
        })
        $script:StepName = $null
    }
}

# Assigning a role twice is an error rather than a no-op, so every grant checks first. Returns
# $true when it actually created one, which is the only case that has to propagate before use.
function Grant-Role {
    param(
        [Parameter(Mandatory)][string]$PrincipalId,
        [Parameter(Mandatory)][string]$Role,
        [Parameter(Mandatory)][string]$Scope,
        [string]$PrincipalType = 'ServicePrincipal'
    )

    $existing = Invoke-Az @('role', 'assignment', 'list',
        '--assignee', $PrincipalId, '--scope', $Scope, '--role', $Role, '--query', '[].id', '-o', 'tsv')

    if (-not [string]::IsNullOrWhiteSpace($existing)) {
        Write-Host "    '$Role' already assigned, no wait needed"
        return $false
    }

    Invoke-Az @('role', 'assignment', 'create',
        '--assignee-object-id', $PrincipalId,
        '--assignee-principal-type', $PrincipalType,
        '--scope', $Scope,
        '--role', $Role) | Out-Null

    Write-Host "    '$Role' assigned; it needs to propagate"
    return $true
}

# --value would put the secret on a command line, where other local users can read it. --file is
# the only alternative az offers, so the value goes through a temp file that is deleted straight
# away. RBAC on a vault can take a while to reach the data plane, hence the retry.
function Set-VaultSecret {
    param(
        [Parameter(Mandatory)][string]$VaultName,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    $file = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
    try {
        # WriteAllText, not Set-Content: a trailing newline would become part of the secret.
        [IO.File]::WriteAllText($file, $Value)

        for ($attempt = 1; ; $attempt++) {
            try {
                Invoke-Az @('keyvault', 'secret', 'set', '--vault-name', $VaultName,
                    '-n', $Name, '--file', $file, '--encoding', 'utf-8', '-o', 'none') | Out-Null
                return
            }
            catch {
                if ($attempt -ge 8) { throw }
                Write-Host "    waiting for the vault role assignment to reach the data plane (attempt $attempt)"
                Start-Sleep -Seconds 10
            }
        }
    }
    finally {
        Remove-Item $file -Force -ErrorAction SilentlyContinue
    }
}

# A self-signed certificate, because nothing else can be issued without a public DNS zone to
# prove control of. az takes a policy only as a file, which suits us: the policy is long.
function New-VaultCertificate {
    param(
        [Parameter(Mandatory)][string]$VaultName,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Subject
    )

    # exportable is the setting that matters: without it the private key cannot be read back
    # through the secret of the same name, and a certificate the sink cannot download in full is
    # no use for terminating TLS.
    $policy = [ordered]@{
        issuerParameters          = @{ name = 'Self' }
        keyProperties             = [ordered]@{
            exportable = $true
            keySize    = 2048
            keyType    = 'RSA'
            reuseKey   = $false
        }
        secretProperties          = @{ contentType = 'application/x-pkcs12' }
        x509CertificateProperties = [ordered]@{
            subject                 = "CN=$Subject"
            subjectAlternativeNames = @{ dnsNames = @($Subject) }
            validityInMonths        = 12
            keyUsage                = @('digitalSignature', 'keyEncipherment')
            ekus                    = @('1.3.6.1.5.5.7.3.1')
        }
        lifetimeActions           = @(
            @{
                trigger = @{ daysBeforeExpiry = 30 }
                action  = @{ actionType = 'AutoRenew' }
            }
        )
    }

    $file = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
    try {
        [IO.File]::WriteAllText($file, ($policy | ConvertTo-Json -Depth 6))

        for ($attempt = 1; ; $attempt++) {
            try {
                Invoke-Az @('keyvault', 'certificate', 'create', '--vault-name', $VaultName,
                    '-n', $Name, '--policy', "@$file", '-o', 'none') | Out-Null
                return
            }
            catch {
                if ($attempt -ge 8) { throw }
                Write-Host "    waiting for the vault role assignment to reach the data plane (attempt $attempt)"
                Start-Sleep -Seconds 10
            }
        }
    }
    finally {
        Remove-Item $file -Force -ErrorAction SilentlyContinue
    }
}

function Test-VaultSecret {
    param([Parameter(Mandatory)][string]$VaultName, [Parameter(Mandatory)][string]$Name)

    & az keyvault secret show --vault-name $VaultName -n $Name --query id -o tsv 2>$null | Out-Null
    $found = $LASTEXITCODE -eq 0
    $global:LASTEXITCODE = 0
    return $found
}

# Alphanumeric only: the password travels through SMTP AUTH, shells and config files, and every
# class of quoting bug it could hit is avoidable for free.
function New-SmtpPassword {
    $bytes = [byte[]]::new(64)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $alphanumeric = [Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', ''
    return $alphanumeric.Substring(0, 32)
}

if ($Exposure -eq 'Public' -and -not $DnsLabel) {
    throw 'A -DnsLabel is required when -Exposure is Public.'
}

# Senders verify the certificate against the name they connect to. A public deployment has one
# that is known in advance; a private one has only an IP, so it gets a stable placeholder that
# whoever runs it can point DNS at -- or override with -CertificateSubject.
if (-not $CertificateSubject) {
    $CertificateSubject = if ($Exposure -eq 'Public') {
        "$DnsLabel.$Location.azurecontainer.io"
    }
    else {
        "$NamePrefix.internal"
    }
}

# Nothing to decide any more: outside the Development environment the sink refuses to start
# without a credential pair and a certificate, and refuses mail from a session that has not used
# both. The container has no DOTNET_ENVIRONMENT set, so it runs as Production.

if ($Subscription) {
    Invoke-Az @('account', 'set', '--subscription', $Subscription) | Out-Null
}

$account = (Invoke-Az @('account', 'show', '--query', '[id,name]', '-o', 'tsv')) -split "`t"
$subscriptionId = $account[0]
$subscriptionName = $account[1]

Write-Host "Subscription : $subscriptionName ($subscriptionId)"
Write-Host "Resource group: $ResourceGroup ($Location)"
Write-Host "Exposure      : $Exposure"
Write-Host ''

# Neither the subscription nor the resource group was named, so this run is about to create a
# registry, a storage account holding captured mail, and a container group wherever the az CLI
# happens to be pointed. Confirm the target rather than discovering it afterwards.
if (-not $Force -and
    -not $PSBoundParameters.ContainsKey('Subscription') -and
    -not $PSBoundParameters.ContainsKey('ResourceGroup')) {

    if ((Read-Host "Deploy to '$ResourceGroup' in subscription '$subscriptionName'? [y/N]") -notmatch '^\s*y(es)?\s*$') {
        Write-Host 'Nothing deployed. Name a target with -Subscription / -ResourceGroup, or pass -Force to skip this prompt.'
        return
    }

    Write-Host ''
}

# Deterministic suffix so repeat runs hit the same globally-unique names.
$seed = "$subscriptionId/$ResourceGroup/$NamePrefix".ToLowerInvariant()
$hash = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($seed))
$suffix = -join ($hash[0..3] | ForEach-Object { $_.ToString('x2') })

$registryName = "$NamePrefix$suffix"                       # 5-50 alphanumeric
$storageName = "$NamePrefix$suffix"                        # 3-24 lowercase alphanumeric
$identityName = "id-$NamePrefix-$suffix"
$vaultName = "kv-$NamePrefix-$suffix"                      # 3-24 alphanumeric and hyphens
$containerGroup = "aci-$NamePrefix"
$shareName = 'mail'

$usernameSecret = 'mailsink-smtp-username'
$passwordSecret = 'mailsink-smtp-password'
$tlsCertificate = 'mailsink-smtp-tls'

if ($storageName.Length -gt 24) { throw "-NamePrefix is too long; the storage account name '$storageName' exceeds 24 characters." }
if ($vaultName.Length -gt 24) { throw "-NamePrefix is too long; the key vault name '$vaultName' exceeds 24 characters." }

$projectPath = Join-Path $PSScriptRoot '..\src\MailSink' | Resolve-Path

# Tag the image by the content that goes into it rather than the clock, so an unchanged source
# tree resolves to a tag that already exists and the build can be skipped entirely. A timestamp
# tag would force a full ~3 minute rebuild on every run, including infrastructure-only changes.
function Get-SourceHash {
    param([Parameter(Mandatory)][string]$Root)

    $ignored = @('bin', 'obj', 'mail')
    $files = Get-ChildItem -Path $Root -Recurse -File |
        Where-Object {
            $relative = [IO.Path]::GetRelativePath($Root, $_.FullName)
            $segments = $relative -split '[\\/]'
            -not ($segments | Where-Object { $ignored -contains $_ })
        } |
        Sort-Object { [IO.Path]::GetRelativePath($Root, $_.FullName) }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $buffer = [System.IO.MemoryStream]::new()
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($Root, $file.FullName)
            $nameBytes = [Text.Encoding]::UTF8.GetBytes($relative)
            $buffer.Write($nameBytes, 0, $nameBytes.Length)
            $contentHash = [System.Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($file.FullName))
            $buffer.Write($contentHash, 0, $contentHash.Length)
        }

        $digest = $sha.ComputeHash($buffer.ToArray())
        return (-join ($digest[0..5] | ForEach-Object { $_.ToString('x2') }))
    }
    finally {
        $sha.Dispose()
    }
}

$sourceHash = Get-SourceHash -Root $projectPath
$imageTag = "mail-sink:$sourceHash"

# The resource group, registry, storage account, identity and vault do not depend on one another,
# and each az call costs ~0.8s of CLI startup plus an ARM round trip. Run them at once so the
# block costs about as much as its slowest member rather than their sum.
#
# Each job also captures the create output: "az acr create" already returns loginServer and id,
# and "az identity create" returns id, principalId and clientId, so the follow-up "az ... show"
# calls this script used to make were pure overhead.
Step 'Resource group, registry, storage, identity and vault (in parallel)'

$adminEnabled = $UseAdminCredentials.ToString().ToLowerInvariant()

# Everything below lives in the resource group, so this one cannot be parallelised with them.
Invoke-Az @('group', 'create', '-n', $ResourceGroup, '-l', $Location) | Out-Null

# Each job starts with ErrorActionPreference = Continue. Thread jobs inherit the script's 'Stop',
# which turns anything az writes to stderr -- including the harmless --min-tls-version deprecation
# warning -- into a terminating error. Exit codes are what these commands are judged on.
$jobs = [ordered]@{
    registry = Start-ThreadJob -ArgumentList $ResourceGroup, $registryName, $Location, $adminEnabled -ScriptBlock {
        param($rg, $name, $loc, $admin)
        $ErrorActionPreference = 'Continue'
        $json = az acr create -g $rg -n $name --sku Basic -l $loc --admin-enabled $admin -o json 2>$null
        if ($LASTEXITCODE -ne 0) { throw "az acr create failed ($LASTEXITCODE)" }
        return ($json | ConvertFrom-Json)
    }

    storage = Start-ThreadJob -ArgumentList $ResourceGroup, $storageName, $Location, $shareName -ScriptBlock {
        param($rg, $name, $loc, $share)
        $ErrorActionPreference = 'Continue'
        az storage account create -g $rg -n $name -l $loc --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --allow-blob-public-access false -o none 2>$null
        if ($LASTEXITCODE -ne 0) { throw "az storage account create failed ($LASTEXITCODE)" }

        az storage share-rm create -g $rg --storage-account $name -n $share --quota 100 -o none 2>$null
        if ($LASTEXITCODE -ne 0) { throw "az storage share-rm create failed ($LASTEXITCODE)" }

        $key = az storage account keys list -g $rg --account-name $name --query '[0].value' -o tsv 2>$null
        if ($LASTEXITCODE -ne 0) { throw "az storage account keys list failed ($LASTEXITCODE)" }
        return $key
    }

    identity = Start-ThreadJob -ArgumentList $ResourceGroup, $identityName, $Location -ScriptBlock {
        param($rg, $name, $loc)
        $ErrorActionPreference = 'Continue'
        $json = az identity create -g $rg -n $name -l $loc -o json 2>$null
        if ($LASTEXITCODE -ne 0) { throw "az identity create failed ($LASTEXITCODE)" }
        return ($json | ConvertFrom-Json)
    }

    vault = Start-ThreadJob -ArgumentList $ResourceGroup, $vaultName, $Location -ScriptBlock {
        param($rg, $name, $loc)
        $ErrorActionPreference = 'Continue'
        # Unlike az's other create commands, "keyvault create" errors instead of returning the
        # existing vault, so a second run would fail on a resource the script itself made.
        $id = az keyvault show -g $rg -n $name --query id -o tsv 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($id)) {
            return [pscustomobject]@{ Id = $id; Existed = $true }
        }

        $json = az keyvault create -g $rg -n $name -l $loc --sku standard --enable-rbac-authorization true --retention-days 7 -o json 2>$null
        if ($LASTEXITCODE -ne 0) { throw "az keyvault create failed ($LASTEXITCODE)" }
        return [pscustomobject]@{ Id = ($json | ConvertFrom-Json).id; Existed = $false }
    }

    # Independent of all of the above, and the answer is not needed until the build step.
    image = Start-ThreadJob -ArgumentList $registryName, $imageTag -ScriptBlock {
        param($registry, $tag)
        $ErrorActionPreference = 'Continue'
        az acr repository show --name $registry --image $tag -o none 2>$null
        return ($LASTEXITCODE -eq 0)
    }
}

try {
    $results = @{}
    foreach ($name in @($jobs.Keys)) {
        $results[$name] = Receive-Job -Job $jobs[$name] -Wait -AutoRemoveJob
    }
}
catch {
    foreach ($job in $jobs.Values) {
        if ($job.State -eq 'Running') { Stop-Job $job -ErrorAction SilentlyContinue }
    }
    throw
}

$loginServer = $results.registry.loginServer
$registryId = $results.registry.id

# Handed straight to az container create; never echoed, never written to disk.
$storageKey = $results.storage

$identityId = $results.identity.id
$principalId = $results.identity.principalId
$identityClientId = $results.identity.clientId

$vaultId = $results.vault.Id
$vaultHost = "$vaultName.vault.azure.net"
if ($results.vault.Existed) { Write-Host '    vault already existed, reused' }

$imageExists = $results.image
$global:LASTEXITCODE = 0

if ($imageExists -and -not $ForceBuild) {
    Step "Image $imageTag already in the registry, skipping the build (use -ForceBuild to override)"
}
else {
    Step "Building image $imageTag"
    # az resolves --file against the current directory rather than the build context, so build from
    # inside the project folder and let both default to '.'.
    Push-Location $projectPath
    try {
        Invoke-Az @('acr', 'build', '-r', $registryName, '-t', $imageTag, '.') | Out-Null
    }
    finally {
        Pop-Location
    }
}

# RBAC rather than access policies: the same role assignments as everything else in the script,
# and the only model still recommended for new vaults. The vault itself was created above.
Step "Key vault secrets ($vaultName)"

# Creating a vault does not grant its creator any access to the secrets inside it, so whoever is
# running this has to be given the data-plane role before the secrets can be written.
$callerObjectId = & az ad signed-in-user show --query id -o tsv 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($callerObjectId)) {
    $callerType = 'User'
}
else {
    # Signed in as a service principal, which has no "signed-in user".
    $global:LASTEXITCODE = 0
    $callerAppId = Invoke-Az @('account', 'show', '--query', 'user.name', '-o', 'tsv')
    $callerObjectId = Invoke-Az @('ad', 'sp', 'show', '--id', $callerAppId, '--query', 'id', '-o', 'tsv')
    $callerType = 'ServicePrincipal'
}
$global:LASTEXITCODE = 0

Grant-Role -PrincipalId $callerObjectId -Role 'Key Vault Secrets Officer' -Scope $vaultId -PrincipalType $callerType | Out-Null

# The username is not sensitive, but keeping it beside the password means the credential pair is
# read, rotated and audited in one place instead of two. Written only when it actually changes,
# so re-running does not stack up identical secret versions.
#
# Set-VaultSecret is also the only call here that retries while the role assignment above
# propagates, and the username is always written on a fresh vault, so by the time the password is
# looked at the data plane is known to work. That matters for Test-VaultSecret, whose "not found"
# would otherwise be indistinguishable from "not allowed yet" and would regenerate a password
# that already exists.
$currentUsername = & az keyvault secret show --vault-name $vaultName -n $usernameSecret --query value -o tsv 2>$null
$global:LASTEXITCODE = 0

if ($currentUsername -ne $SmtpUsername) {
    Set-VaultSecret -VaultName $vaultName -Name $usernameSecret -Value $SmtpUsername
}
else {
    Write-Host "    username '$SmtpUsername' already stored"
}

$passwordExists = Test-VaultSecret -VaultName $vaultName -Name $passwordSecret

if ($SmtpPassword) {
    $secretValue = $SmtpPassword
    Write-Host '    storing the password from -SmtpPassword'
}
elseif ($RotatePassword) {
    $secretValue = New-SmtpPassword
    Write-Host '    rotating to a newly generated password'
}
elseif (-not $passwordExists) {
    $secretValue = New-SmtpPassword
    Write-Host '    generating a password (32 random alphanumeric characters)'
}
else {
    # Re-running must not silently change the credential every deployed sender is configured with.
    $secretValue = $null
    Write-Host '    keeping the password already in the vault'
}

try {
    if ($secretValue) {
        Set-VaultSecret -VaultName $vaultName -Name $passwordSecret -Value $secretValue
    }
}
finally {
    $secretValue = $null
    $SmtpPassword = $null
}

$newVaultAssignment = Grant-Role -PrincipalId $principalId -Role 'Key Vault Secrets User' -Scope $vaultId

Step "Key vault certificate ($tlsCertificate)"

Grant-Role -PrincipalId $callerObjectId -Role 'Key Vault Certificates Officer' -Scope $vaultId -PrincipalType $callerType | Out-Null

# Reissuing on every run would hand senders a new certificate to trust each time, so the stored
# one is kept unless it is for a different name than the deployment now uses.
$currentSubject = & az keyvault certificate show --vault-name $vaultName -n $tlsCertificate --query 'policy.x509CertificateProperties.subject' -o tsv 2>$null
$global:LASTEXITCODE = 0

if ($currentSubject -eq "CN=$CertificateSubject") {
    Write-Host "    certificate for '$CertificateSubject' already stored"
}
else {
    if ($currentSubject) {
        Write-Host "    reissuing: the stored certificate is '$currentSubject', not 'CN=$CertificateSubject'"
    }
    else {
        Write-Host "    issuing a self-signed certificate for '$CertificateSubject'"
    }

    New-VaultCertificate -VaultName $vaultName -Name $tlsCertificate -Subject $CertificateSubject
}

# The sink downloads the private key through the secret backing the certificate, so it needs both
# roles: Certificate User to see the certificate, Secrets User (granted just above) to read it.
if (Grant-Role -PrincipalId $principalId -Role 'Key Vault Certificate User' -Scope $vaultId) {
    $newVaultAssignment = $true
}

# The container is handed vault URIs, not secrets. Versionless, so restarting picks up a rotation.
$usernameReference = "@Microsoft.KeyVault(SecretUri=https://$vaultHost/secrets/$usernameSecret)"
$passwordReference = "@Microsoft.KeyVault(SecretUri=https://$vaultHost/secrets/$passwordSecret)"
$certificateUri = "https://$vaultHost/certificates/$tlsCertificate"

$environment = [ordered]@{
    'MailSink__MailDirectory'                     = '/mail'
    # The image already listens on these, but naming them keeps the published ports and the
    # listener from drifting apart when either is changed.
    'MailSink__StartTlsPorts__0'                  = "$StartTlsPort"
    'MailSink__ImplicitTlsPorts__0'               = "$ImplicitTlsPort"
    # Not published, so it is reachable by the probe and not by a sender.
    'MailSink__HealthPort'                        = "$HealthPort"
    'TZ'                                          = $TimeZone
    'MailSink__Username'                          = $usernameReference
    'MailSink__Password'                          = $passwordReference
    # Not a secret: the certificate's private key is fetched through the managed identity.
    'MailSink__Tls__KeyVaultCertificateUri'       = $certificateUri
    # Naming the one vault the sink may read means a reference pointing anywhere else is refused
    # rather than fetched.
    'MailSink__KeyVault__AllowedHosts__0'         = $vaultHost
    # A container group can carry several identities; the default credential cannot guess.
    'MailSink__KeyVault__ManagedIdentityClientId' = $identityClientId
}

if ($UseAdminCredentials) {
    Step 'Registry admin credentials'
    $registryUser = Invoke-Az @('acr', 'credential', 'show', '-n', $registryName, '--query', 'username', '-o', 'tsv')
    $registryPassword = Invoke-Az @('acr', 'credential', 'show', '-n', $registryName, '--query', 'passwords[0].value', '-o', 'tsv')
    $registryCredentials = @(
        [ordered]@{ server = $loginServer; username = $registryUser; password = $registryPassword }
    )

    # Nothing new to propagate on this path; the vault grants were made further up.
    $newRoleAssignment = $false
}
else {
    Step 'AcrPull for the image pull'
    # Only a brand new assignment needs to propagate before the pull can use it. The wait is
    # skipped entirely on every later run, which is where the minute used to be spent.
    $newRoleAssignment = Grant-Role -PrincipalId $principalId -Role 'AcrPull' -Scope $registryId
    $registryCredentials = @(
        [ordered]@{ server = $loginServer; identity = $identityId }
    )
}

$ports = @(
    [ordered]@{ protocol = 'TCP'; port = $StartTlsPort },
    [ordered]@{ protocol = 'TCP'; port = $ImplicitTlsPort }
)

# The container group is deployed from a file rather than from switches, because a liveness probe
# can only be expressed that way -- ACI has no CLI flag for one, and without a probe nothing can
# tell a wedged container from a healthy one. It also keeps the storage account key and the
# registry password off the command line, which the switch form could not do: they now travel in
# a temp file that is deleted straight after.
$group = [ordered]@{
    apiVersion = '2021-10-01'
    location   = $Location
    name       = $containerGroup
    type       = 'Microsoft.ContainerInstance/containerGroups'
    identity   = [ordered]@{
        type                   = 'UserAssigned'
        # Attached whether or not it is also pulling the image: the sink needs it to read the
        # credentials and the certificate out of the vault.
        userAssignedIdentities = @{ $identityId = @{} }
    }
    properties = [ordered]@{
        osType                   = 'Linux'
        restartPolicy            = 'Always'
        imageRegistryCredentials = $registryCredentials
        containers               = @(
            [ordered]@{
                name       = 'mail-sink'
                properties = [ordered]@{
                    image                = "$loginServer/$imageTag"
                    resources            = @{ requests = [ordered]@{ cpu = [double]$Cpu; memoryInGB = [double]$Memory } }
                    ports                = $ports
                    environmentVariables = @(
                        $environment.Keys | ForEach-Object { [ordered]@{ name = $_; value = $environment[$_] } }
                    )
                    volumeMounts         = @([ordered]@{ name = 'mail'; mountPath = '/mail' })
                    livenessProbe        = [ordered]@{
                        httpGet             = [ordered]@{ path = '/healthz'; port = $HealthPort; scheme = 'http' }
                        # The sink has to reach Key Vault for its certificate before it answers,
                        # and on a cold start the role assignment may still be propagating, so the
                        # first check waits rather than restarting a container that is fine.
                        initialDelaySeconds = 30
                        periodSeconds       = 30
                        timeoutSeconds      = 5
                        failureThreshold    = 3
                    }
                }
            }
        )
        volumes                  = @(
            [ordered]@{
                name      = 'mail'
                azureFile = [ordered]@{
                    shareName          = $shareName
                    storageAccountName = $storageName
                    storageAccountKey  = $storageKey
                }
            }
        )
    }
}

if ($Exposure -eq 'Public') {
    $group.properties.ipAddress = [ordered]@{
        type         = 'Public'
        dnsNameLabel = $DnsLabel
        ports        = $ports
    }
}
else {
    Step "Virtual network ($VNetName/$SubnetName)"

    # az container create --vnet would create these on the fly, but the file form takes a subnet
    # id and cannot, so they are created here. Both checks are idempotent.
    $vnetId = & az network vnet show -g $ResourceGroup -n $VNetName --query id -o tsv 2>$null
    $global:LASTEXITCODE = 0

    if (-not $vnetId) {
        Invoke-Az @('network', 'vnet', 'create', '-g', $ResourceGroup, '-n', $VNetName,
            '-l', $Location, '--address-prefix', $VNetAddressPrefix, '-o', 'none') | Out-Null
        Write-Host "    created $VNetName ($VNetAddressPrefix)"
    }
    else {
        Write-Host "    $VNetName already exists"
    }

    $subnetId = & az network vnet subnet show -g $ResourceGroup --vnet-name $VNetName -n $SubnetName --query id -o tsv 2>$null
    $global:LASTEXITCODE = 0

    if (-not $subnetId) {
        # The delegation is what makes a subnet usable by a container group at all.
        $subnetId = Invoke-Az @('network', 'vnet', 'subnet', 'create', '-g', $ResourceGroup,
            '--vnet-name', $VNetName, '-n', $SubnetName, '--address-prefixes', $SubnetAddressPrefix,
            '--delegations', 'Microsoft.ContainerInstance/containerGroups', '--query', 'id', '-o', 'tsv')
        Write-Host "    created $SubnetName ($SubnetAddressPrefix), delegated to ACI"
    }
    else {
        Write-Host "    $SubnetName already exists"
    }

    $group.properties.ipAddress = [ordered]@{ type = 'Private'; ports = $ports }
    $group.properties.subnetIds = @([ordered]@{ id = $subnetId })
}

Step "Container group ($containerGroup)"
try {
    # Recreating the group is by far the slowest thing the script does -- ACI tears the old one
    # down and pulls the image again, three minutes for an unchanged deployment. Compare what is
    # already running against what we are about to ask for, and skip when they match.
    $desired = [ordered]@{
        image       = "$loginServer/$imageTag"
        cpu         = [double]$Cpu
        memory      = [double]$Memory
        ports       = @([int]$StartTlsPort, [int]$ImplicitTlsPort)
        # The one built above, so the two can no longer drift apart. Keeping a second copy here
        # is what used to make an unchanged deployment recreate itself on every run.
        env         = $environment
    }

    $current = $null
    if (-not $ForceRecreate) {
        $json = & az container show -g $ResourceGroup -n $containerGroup -o json 2>$null
        $global:LASTEXITCODE = 0
        if ($json) { $current = $json | ConvertFrom-Json }
    }

    $unchanged = $false
    if ($current -and $current.containers.Count -eq 1) {
        $c = $current.containers[0]
        $currentEnv = @{}
        foreach ($v in @($c.environmentVariables)) { $currentEnv[$v.name] = $v.value }

        $envMatches = $currentEnv.Count -eq $desired.env.Count -and
            -not ($desired.env.Keys | Where-Object { $currentEnv[$_] -ne $desired.env[$_] })

        $unchanged = $c.image -eq $desired.image -and
            [double]$c.resources.requests.cpu -eq $desired.cpu -and
            [double]$c.resources.requests.memoryInGb -eq $desired.memory -and
            -not (@($desired.ports) | Where-Object { @($c.ports.port) -notcontains $_ }) -and
            $current.instanceView.state -eq 'Running' -and
            $envMatches
    }

    if ($unchanged) {
        Write-Host '    already running this image and configuration, leaving it alone'
        Write-Host '    (use -ForceRecreate to redeploy it anyway)'
    }
    else {
        # A freshly created role assignment may not have reached the registry yet. Rather than
        # pre-emptively sleeping a flat minute, attempt the create and retry only if it actually
        # fails -- usually the first attempt succeeds and nothing is waited for at all.
        $attempts = if ($newRoleAssignment) { 6 } else { 1 }

        # JSON, which az parses with a YAML loader that accepts it. Building the document as a
        # PowerShell object and converting is far harder to get subtly wrong than emitting YAML
        # by hand, and the quoting rules stop mattering entirely.
        $groupFile = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName() + '.json')

        try {
            [IO.File]::WriteAllText($groupFile, ($group | ConvertTo-Json -Depth 12))

            for ($attempt = 1; ; $attempt++) {
                try {
                    Invoke-Az @('container', 'create', '-g', $ResourceGroup, '--file', $groupFile, '-o', 'none') | Out-Null
                    break
                }
                catch {
                    if ($attempt -ge $attempts) { throw }

                    Write-Host "    attempt $attempt failed (likely the role assignment propagating); retrying in 15s"
                    Start-Sleep -Seconds 15
                }
            }
        }
        finally {
            # The file carries the storage account key, so it does not outlive the call.
            Remove-Item $groupFile -Force -ErrorAction SilentlyContinue
        }
    }
}
finally {
    # Drop the references once the key has served its purpose. This is tidiness, not erasure --
    # .NET strings are immutable, so the bytes stay in memory until the process ends.
    $group = $null
    $storageKey = $null
    $registryPassword = $null
}

if ($RestrictStorageNetwork) {
    if ($Exposure -eq 'Public') {
        Write-Warning '-RestrictStorageNetwork needs a subnet to allow, which -Exposure Public does not create. Skipped.'
    }
    else {
        Step 'Restricting the storage account to the container subnet'
        Invoke-Az @('network', 'vnet', 'subnet', 'update', '-g', $ResourceGroup, '--vnet-name', $VNetName,
            '-n', $SubnetName, '--service-endpoints', 'Microsoft.Storage') | Out-Null
        $subnetId = Invoke-Az @('network', 'vnet', 'subnet', 'show', '-g', $ResourceGroup,
            '--vnet-name', $VNetName, '-n', $SubnetName, '--query', 'id', '-o', 'tsv')
        Invoke-Az @('storage', 'account', 'network-rule', 'add', '-g', $ResourceGroup,
            '--account-name', $storageName, '--subnet', $subnetId) | Out-Null
        Invoke-Az @('storage', 'account', 'update', '-g', $ResourceGroup, '-n', $storageName,
            '--default-action', 'Deny') | Out-Null

        Write-Host '    the share is now reachable only from that subnet. To read the mail from your'
        Write-Host '    own machine, allow your public IP as well:'
        Write-Host "      az storage account network-rule add -g $ResourceGroup --account-name $storageName --ip-address <your-ip>"
    }
}

Stop-Steps

Write-Host ''
Write-Host 'Deployed.' -ForegroundColor Green
Write-Host ''
Write-Host 'Time spent:'
$script:StepTimings |
    Sort-Object Seconds -Descending |
    ForEach-Object { Write-Host ("  {0,6:N1}s  {1}" -f $_.Seconds, $_.Step) }
Write-Host ("  {0,6:N1}s  total" -f ($script:StepTimings | Measure-Object Seconds -Sum).Sum)

if ($Exposure -eq 'Public') {
    $fqdn = Invoke-Az @('container', 'show', '-g', $ResourceGroup, '-n', $containerGroup, '--query', 'ipAddress.fqdn', '-o', 'tsv')
    Write-Host "  SMTP host : $fqdn"
}
else {
    $ip = Invoke-Az @('container', 'show', '-g', $ResourceGroup, '-n', $containerGroup, '--query', 'ipAddress.ip', '-o', 'tsv')
    Write-Host "  SMTP host : $ip (private, reachable from $VNetName only)"
}

Write-Host "  SMTP port : $StartTlsPort (STARTTLS), $ImplicitTlsPort (implicit TLS)"
Write-Host "  SMTP user : $SmtpUsername"
Write-Host '  Auth      : required, over TLS only'
Write-Host "  Cert for  : $CertificateSubject (self-signed, TLS 1.2+)"
Write-Host "  Health    : liveness probe on http://<container>:$HealthPort/healthz"
Write-Host "  Mail share: $storageName/$shareName"
Write-Host ''
Write-Host 'The password is in the key vault; the container reads it through its managed identity.'
Write-Host 'Read it back when you need to configure a sender:'
Write-Host "  az keyvault secret show --vault-name $vaultName -n $passwordSecret --query value -o tsv"
Write-Host ''
Write-Host 'The certificate is self-signed, so senders must be told to trust it. Export the public'
Write-Host 'half and install it wherever the sending application keeps its trusted roots:'
Write-Host "  az keyvault certificate download --vault-name $vaultName -n $tlsCertificate -f mailsink.crt"
Write-Host ''
Write-Host 'Senders must also reach it by the name on the certificate. Connecting to the bare IP'
Write-Host 'will fail host name validation, which is the check doing the work here.'
Write-Host ''

if ($newVaultAssignment) {
    Write-Host 'The vault role assignment was created just now. Until it propagates the sink cannot read' -ForegroundColor Yellow
    Write-Host 'its password and will restart a few times; that settles on its own within a minute or two.' -ForegroundColor Yellow
    Write-Host ''
}

Write-Host 'Read the captured mail by mapping the share as a drive. Keep the key in a variable and'
Write-Host 'out of your shell history -- net use would put it on a command line:'
Write-Host "  `$key = az storage account keys list -g $ResourceGroup --account-name $storageName --query '[0].value' -o tsv"
Write-Host "  `$cred = [pscredential]::new('localhost\$storageName', (ConvertTo-SecureString `$key -AsPlainText -Force))"
Write-Host "  New-PSDrive -Name Z -PSProvider FileSystem -Root \\$storageName.file.core.windows.net\$shareName -Credential `$cred"
Write-Host "  `$key = `$null"
Write-Host ''
Write-Host 'Follow the log with:'
Write-Host "  az container logs -g $ResourceGroup -n $containerGroup --follow"
