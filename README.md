# mail sink

A self-hosted SMTP server that accepts mail from your applications and writes every message to
disk as an `.eml` file. Nothing is relayed, so no mail ever reaches a real mailbox.

Double-click an `.eml` to open it in Outlook, Thunderbird, or Windows Mail — attachments, HTML
body and headers included.

> [!WARNING]
> **This is a development tool. Keep it on a trusted network.**
>
> It is a deliberately indiscriminate mail server: it accepts mail without authentication, accepts
> any recipient, speaks plain SMTP with no TLS, and stores every message unencrypted on disk. By
> default it listens on `0.0.0.0`, so it is reachable from your whole network, not just localhost.
>
> It cannot be used as an open relay — nothing is ever forwarded — but anything it captures is
> readable by anyone who can reach the host or its storage, and any SMTP credentials your app sends
> cross the network in the clear. Point test systems at it, not production ones, and set
> `MailSink:ListenAddress` to `127.0.0.1` if you only need it locally.
>
> See [SECURITY.md](SECURITY.md) for what is deliberate and what is worth reporting.

## Run it

```powershell
dotnet run --project src/MailSink
```

The SMTP listener binds to `0.0.0.0:1025` and writes to `src/MailSink/mail/<yyyy-MM-dd>/`.

## Point an app at it

| Setting | Value                                                      |
| ------- | ---------------------------------------------------------- |
| Host    | `localhost` (or the machine's IP / `host.docker.internal`) |
| Port    | `1025`                                                     |
| SSL/TLS | off                                                        |
| Auth    | none needed; any username/password is accepted             |

Set `MailSink:Username` and `MailSink:Password` if you want the sink to insist on one specific
pair — see [Authentication](#authentication).

In ASP.NET Core:

```json
"Smtp": { "Host": "localhost", "Port": 1025, "EnableSsl": false }
```

## File names

```text
mail/2026-09-21/100137-883_gijs@example.test_Order-1234-confirmed.eml
      │          │          │                 └── subject, slugged and capped at 60 chars
      │          │          └── first envelope recipient
      │          └── time received (HHmmss-fff)
      └── date received
```

A subject that cannot be parsed is simply left out of the name; the message is still stored byte
for byte.

## Configuration

Set in `src/MailSink/appsettings.json`, or override with environment variables using the
`MailSink__` prefix (e.g. `MailSink__Ports__0=25`).

| Key                     | Default     | Meaning                                              |
| ----------------------- | ----------- | ---------------------------------------------------- |
| `MailDirectory`         | `mail`      | Where `.eml` files go. Relative to the content root. |
| `ServerName`            | `mail-sink` | Name reported in the SMTP greeting.                  |
| `ListenAddress`         | `0.0.0.0`   | Bind address. Use `127.0.0.1` to keep it local-only. |
| `Ports`                 | `[1025]`    | Ports to listen on. Port 25 is privileged on Linux.  |
| `MaxMessageSize`        | `26214400`  | Bytes. Larger messages are rejected with 552.        |
| `AllowAnyCredentials`   | `true`      | Advertise AUTH and accept any credentials.           |
| `Username`              | *(empty)*   | Require this exact username. Empty accepts any.      |
| `Password`              | *(empty)*   | Password for `Username`. Required once it is set.    |
| `RequireAuthentication` | `false`     | Refuse `MAIL FROM` until the session has AUTHed.     |
| `MaxConcurrentSessions` | `64`        | Connections served at once; `0` removes the limit.   |
| `GroupByDate`           | `true`      | Write into a `yyyy-MM-dd` subfolder per day.         |

`MaxConcurrentSessions` is what bounds memory: each in-flight message is held whole, up to
`MaxMessageSize`. Connections beyond the limit are dropped until one finishes.

## Authentication

Three modes, chosen by configuration:

| Configuration                                  | Behaviour                                            |
| ---------------------------------------------- | ---------------------------------------------------- |
| *(default)* `AllowAnyCredentials: true`        | AUTH advertised, any username/password accepted.     |
| `Username` + `Password` set                    | AUTH advertised, only that pair accepted.            |
| `AllowAnyCredentials: false`, no `Username`    | AUTH not advertised, no authenticator registered.    |

```json
"MailSink": { "Username": "app", "Password": "s3cret", "RequireAuthentication": true }
```

Or as environment variables: `MailSink__Username=app`, `MailSink__Password=s3cret`.

Setting `Username` wins over `AllowAnyCredentials` — the explicit pair is the more specific
instruction, so it is not silently cancelled by the flag. A `Username` without a `Password` (or
the other way round) fails at startup rather than quietly going unenforced.

**`RequireAuthentication` is the switch that makes credentials bite.** With it off — the default —
a wrong pair gets `535`, but the session simply carries on and delivers the message
unauthenticated, because a sink should not silently drop mail. Turn it on and `MAIL FROM` is
answered with `530 authentication required` until the session has authenticated, which is what
lets a test prove an application really does send the credentials it was configured with.

The credentials are not a security boundary. There is no TLS, so AUTH LOGIN/PLAIN sends them
base64-encoded over a plain socket; treat them as a wiring check on a trusted network, not as
protection. Don't reuse a real password here.

### Where the password comes from

Never `appsettings.json` — that file is committed. There are two supported places, one per
environment:

| Environment | Source                      | Set up by                                   |
| ----------- | --------------------------- | ------------------------------------------- |
| Local       | `appsettings.local.json`    | You, by hand. Gitignored.                   |
| Azure       | Key Vault                   | [deploy/deploy.ps1](deploy/deploy.ps1).     |

**Local.** Copy the example and edit it:

```powershell
Copy-Item src/MailSink/appsettings.local.example.json src/MailSink/appsettings.local.json
```

`appsettings.local.json` is gitignored and excluded from the Docker build context, so it cannot
reach a commit or an image. It overrides `appsettings.json` but still loses to environment
variables and the command line, so a container's `MailSink__Password` is never shadowed by a file
that happened to ship with it.

**Azure.** The deploy script puts the credentials in a key vault and gives the container the
*reference* rather than the secret:

```text
MailSink__Password=@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/mailsink-smtp-password)
```

The sink resolves that at startup through its managed identity, using
[KeyVaultReferenceResolver](https://github.com/gijswalraven/KeyVaultReferenceResolver) — the same
syntax App Service uses. The password itself therefore never appears on a command line, in the
container group's ARM definition, or in `az container show` output. `MailSink:KeyVault:AllowedHosts`
names the one vault the sink may read, so a reference pointing anywhere else is refused rather
than fetched.

Resolution only runs when a value actually starts with `@Microsoft.KeyVault(`, so a local run
never builds a credential chain or looks for a managed identity that isn't there.

## Docker

```powershell
docker compose up -d --build
```

Mail lands in `./mail` on the host. The published port is bound to `127.0.0.1` — Docker's port
publishing bypasses the host firewall, so binding every interface would hand the whole LAN an open
sink. Change it in [compose.yaml](compose.yaml) if other machines need to reach it. The container
runs as a non-root user.

## Azure (Container Instances)

App Service can't host this — its front ends only accept inbound traffic on 80/443, so an SMTP
listener is unreachable there. ACI gives you a raw TCP port, and an Azure Files share keeps the
`.eml` files when the container restarts.

```powershell
./deploy/deploy.ps1
```

`-ResourceGroup` defaults to `rg-mailsink` and `-Location` to `westeurope`. To point it at your own
target without passing them every time, set `MAILSINK_RESOURCE_GROUP` and `MAILSINK_LOCATION` in
your shell profile — the script reads both.

Because a bare `./deploy.ps1` would otherwise create resources in whichever subscription the az CLI
happens to be pointed at, it prints the resolved subscription and asks to continue. Naming a
`-Subscription` or `-ResourceGroup` explicitly skips the prompt, as does `-Force` for unattended
runs.

That creates a Basic container registry, builds the image server-side with `az acr build`, creates
a storage account with a `mail` file share, creates a key vault holding the SMTP credentials, and
runs the container group with the share mounted at `/mail`. Re-running it targets the same
resources — names carry a hash of the subscription and resource group.

**Credentials.** `-SmtpUsername` defaults to `mailsink`. `-SmtpPassword` is generated on the first
run (32 random alphanumeric characters) and kept in the vault; later runs reuse it rather than
silently rotating the credential every configured sender depends on. Pass `-RotatePassword` when
you do want a new one. The script prints how to read it back:

```powershell
az keyvault secret show --vault-name <vault> -n mailsink-smtp-password --query value -o tsv
```

The container receives a vault reference, not the secret, and resolves it through a user-assigned
managed identity granted `Key Vault Secrets User`. Nothing sensitive reaches a command line, the
container group's ARM definition, or `az container show`. The generated password reaches
`az keyvault secret set` through a temp file that is deleted immediately, because `--value` would
put it on a command line.

`-RequireAuthentication` defaults to `$true` for `-Exposure Public` and `$false` for Private. On
the very first deployment the vault role assignment has to propagate before the sink can read its
password, so the container may restart a couple of times for a minute or two; the script says so
when that applies.

**Exposure.** The default is `-Exposure Private`: the container group sits in a VNet with no public
IP, reachable from that VNet, peered networks, or over VPN. `-Exposure Public -DnsLabel <label>`
gives it an `<label>.<region>.azurecontainer.io` FQDN instead. The sink accepts any recipient, so a
public endpoint *will* be found by scanners and filled with junk mail. It can't be abused to relay
— nothing is ever forwarded — but treat public exposure as short-lived. Public exposure therefore
defaults to `-RequireAuthentication $true`, which keeps the casual scanner out; there is still no
TLS, so that is a nuisance filter, not a control.

**Reading the mail.** Map the file share as a drive and double-click the `.eml` files. Keep the key
in a variable — `net use` would put it on a command line, where it lands in your shell history and
is readable by other local users:

```powershell
$key = az storage account keys list -g rg-mailsink --account-name <storage> --query '[0].value' -o tsv
$cred = [pscredential]::new('localhost\<storage>', (ConvertTo-SecureString $key -AsPlainText -Force))
New-PSDrive -Name Z -PSProvider FileSystem -Root \\<storage>.file.core.windows.net\mail -Credential $cred
$key = $null
```

**Locking down the share.** The account key is the only thing protecting captured mail, and the
storage account accepts it from any network by default. `-RestrictStorageNetwork` (Private exposure
only) puts a service endpoint on the container subnet, allows just that subnet, and sets the
default action to Deny. You then add your own IP to keep reading the share — the script prints the
command.

**Image pull.** The script uses a user-assigned managed identity with `AcrPull` (ACI does not
support system-assigned identities for registry pulls). Microsoft's documentation lists a Premium
registry as a prerequisite for that path; if the pull fails against the Basic registry the script
creates, re-run with `-UseAdminCredentials` and it will use the registry admin account instead.
Neither the storage key nor the registry password is written to disk; both are read into a variable
at run time and dropped afterwards. They are passed as arguments to the single `az container create`
call, because ARM has to receive them and az offers no environment-variable equivalent — so command
lines are readable by other local users on a shared deploy machine. The purge script below avoids
this entirely, which matters more there because its scheduled task re-runs unattended every hour.

**Timezone.** A Linux container runs in UTC, which would make the date folders and timestamps UTC
too. The script sets `TZ=Europe/Amsterdam`; override it with `-TimeZone`.

## Windows service

```powershell
dotnet publish src/MailSink -c Release -o C:\Services\MailSink
sc.exe create "mail sink" binPath= "C:\Services\MailSink\MailSink.exe" start= auto
sc.exe start "mail sink"
```

Running as a service means relative paths resolve against the publish folder — set
`MailSink:MailDirectory` to an absolute path such as `C:\MailSink\mail`.

## Tests

```powershell
dotnet test
```

`tests/MailSink.Tests` (XUnit) covers five layers:

- **`MailNaming`** — pure function, so file naming is asserted directly: subject cap, non-ASCII,
  illegal characters, path-separator escaping, collision suffixes.
- **`MailCapture`** — the capture pipeline against an in-memory `IMailWriter` and a
  `FakeTimeProvider`, so names are deterministic and the writer-failure path is exercised.
- **`FixedCredentialUserAuthenticator`** — the credential comparison on its own, so the wrong-pair
  cases are asserted without a socket.
- **Configuration** — that `appsettings.local.json` overrides `appsettings.json` but not the
  environment, and that only a real `@Microsoft.KeyVault(` value is treated as a reference.
- **`SinkEndToEndTests` / `AuthenticationEndToEndTests`** — boot the real listener on a free port
  against a temp folder (`TestSink`) and send through `System.Net.Mail.SmtpClient`, then re-parse
  the resulting `.eml` with MimeKit. The authentication set covers the configured pair, wrong
  pairs, and `RequireAuthentication`.

The seams that make this possible are `IMailWriter`, `IMailCapture`/`IncomingMessage`,
`IMessageMetadataReader` and `SmtpOptionsFactory`; `EmlMessageStore` is only an adapter from
SmtpServer's types onto them.

## Retention

The sink never deletes anything by itself. [deploy/purge-old-mail.ps1](deploy/purge-old-mail.ps1)
removes `.eml` files older than a given age from a local folder, from the Azure Files share, or
both, and drops the date folders once they are empty.

```powershell
# See what would go, without touching anything
./deploy/purge-old-mail.ps1 -Path src/MailSink/mail -WhatIf

# Purge locally and in Azure, anything older than 24 hours
./deploy/purge-old-mail.ps1 -Path src/MailSink/mail `
    -ResourceGroup rg-mailsink -StorageAccount <storage>
```

Make it automatic by registering a scheduled task that runs the same arguments every hour:

```powershell
./deploy/purge-old-mail.ps1 -Path src/MailSink/mail `
    -ResourceGroup rg-mailsink -StorageAccount <storage> -Install
```

Remove it again with `Unregister-ScheduledTask -TaskName 'mail-sink purge' -Confirm:$false`.

| Parameter         | Default | Notes                                                       |
| ----------------- | ------- | ----------------------------------------------------------- |
| `-OlderThanHours` | `24`    | `0` deletes everything currently stored.                    |
| `-RunEveryHours`  | `1`     | Repetition interval used by `-Install`.                     |
| `-WhatIf`         |         | Lists every file that would be deleted and deletes nothing. |

The storage key is fetched from `az` on each run and handed to it through `AZURE_STORAGE_KEY`, so
nothing secret is stored in the task and nothing appears on a command line.

## Alternatives

If you want a sink with a web UI to browse captured mail, [Mailpit](https://github.com/axllent/mailpit)
and [MailHog](https://github.com/mailhog/MailHog) do that well. This one deliberately stops at
`.eml` files on disk, so the mail opens in a real mail client and nothing new has to be learned.

## Contributing, security, licence

- [CONTRIBUTING.md](CONTRIBUTING.md) — scope, style, and how to run the tests.
- [SECURITY.md](SECURITY.md) — what counts as a vulnerability here (several alarming-looking
  properties are deliberate) and how to report one privately.
- [MIT](LICENSE).
