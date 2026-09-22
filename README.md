# mail sink

A self-hosted SMTP server that accepts mail from your applications and writes every message to
disk as an `.eml` file. Nothing is relayed, so no mail ever reaches a real mailbox.

Double-click an `.eml` to open it in Outlook, Thunderbird, or Windows Mail — attachments, HTML
body and headers included.

The sink behaves differently depending on the environment it runs in, and this is the one thing
to understand before anything else:

|              | `Development`        | Anywhere else                                                |
| ------------ | -------------------- | ------------------------------------------------------------ |
| Transport    | plain text on `1025` | STARTTLS on `587`, implicit TLS on `465`                     |
| Credentials  | optional             | **required** — it will not start without them                |
| Certificate  | none                 | **required**, from Key Vault — it will not start without one |
| Mail at rest | unencrypted          | unencrypted                                                  |

Running it locally therefore costs no configuration, and deploying it cannot accidentally reuse
that convenience: outside `Development` a missing certificate, a missing credential pair, or a
configured plain-text port each stop the host at startup.

> [!WARNING]
> **Captured mail is stored unencrypted, in both modes.**
>
> The sink protects the connection and the credentials, not the mailbox. Anything it captures is
> readable by anyone who can reach the host or the storage behind it, so treat the mail directory —
> and the Azure Files share behind it — as the boundary that actually matters. It accepts any
> recipient, and it cannot be used as an open relay, because nothing is ever forwarded.
>
> See [SECURITY.md](SECURITY.md) for what is deliberate and what is worth reporting.

## Run it

```powershell
dotnet run --project src/MailSink
```

`dotnet run` puts the host in the `Development` environment, so the listener binds to
`0.0.0.0:1025` in plain text, takes mail from anyone, and writes to
`src/MailSink/mail/<yyyy-MM-dd>/`. Set `MailSink:ListenAddress` to `127.0.0.1` if you only need it
on your own machine.

## Point an app at it

Locally:

| Setting | Value                                                      |
| ------- | ---------------------------------------------------------- |
| Host    | `localhost` (or the machine's IP / `host.docker.internal`) |
| Port    | `1025`                                                     |
| SSL/TLS | off                                                        |
| Auth    | none                                                       |

Against a deployed sink:

| Setting | Value                                                            |
| ------- | ---------------------------------------------------------------- |
| Host    | the name on the certificate — not an IP, or validation will fail |
| Port    | `587` for STARTTLS, `465` for implicit TLS                       |
| SSL/TLS | required; TLS 1.2 or 1.3                                         |
| Auth    | required, and only offered once the connection is encrypted      |

In ASP.NET Core, locally and then deployed:

```json
"Smtp": { "Host": "localhost", "Port": 1025, "EnableSsl": false }
"Smtp": { "Host": "mailsink.example.test", "Port": 587, "EnableSsl": true }
```

`System.Net.Mail`'s `EnableSsl` means STARTTLS, so it can reach port 587 but not 465. A client that
needs implicit TLS — or a per-connection certificate callback — wants
[MailKit](https://github.com/jstedfast/MailKit), which is what the tests here use.

## File names

```text
mail/2026-09-21/100137-883_gijs@example.test_Order-1234-confirmed.eml
      │          │          │                 └── subject, slugged and capped at 60 bytes
      │          │          └── first envelope recipient
      │          └── time received (HHmmss-fff)
      └── date received
```

A subject that cannot be parsed is simply left out of the name; the message is still stored byte
for byte. Path separators, reserved characters, and control and format characters are replaced
with `-`, so a crafted subject cannot steer the write or smuggle an escape sequence into a log
line. The cap is counted in UTF-8 bytes, not characters, which keeps a subject of emoji inside the
255-byte limit Linux puts on a file name.

## Configuration

Set in `src/MailSink/appsettings.json`, or override with environment variables using the
`MailSink__` prefix (e.g. `MailSink__Ports__0=25`).

| Key                          | Default     | Meaning                                                         |
| ---------------------------- | ----------- | --------------------------------------------------------------- |
| `MailDirectory`              | `mail`      | Where `.eml` files go. Relative to the content root.            |
| `ServerName`                 | `mail-sink` | Name reported in the SMTP greeting.                             |
| `ListenAddress`              | `0.0.0.0`   | Bind address. Use `127.0.0.1` to keep it local-only.            |
| `Ports`                      | `[1025]`    | Plain text. **`Development` only** — a startup error elsewhere. |
| `StartTlsPorts`              | `[587]`     | Require STARTTLS before AUTH.                                   |
| `ImplicitTlsPorts`           | `[465]`     | TLS from the first byte.                                        |
| `MaxMessageSize`             | `26214400`  | Bytes. Larger messages are rejected with 552.                   |
| `Username`                   | *(empty)*   | Required outside `Development`.                                 |
| `Password`                   | *(empty)*   | Password for `Username`. Required once it is set.               |
| `Tls:KeyVaultCertificateUri` | *(empty)*   | Key Vault certificate URI. Required outside `Development`.      |
| `Tls:MinimumProtocol`        | `Tls12`     | `Tls12` (1.2 and 1.3) or `Tls13` (1.3 only).                    |
| `Tls:RefreshInterval`        | `01:00:00`  | How often a rotated certificate is re-read.                     |
| `MaxConcurrentSessions`      | `64`        | Connections served at once; `0` removes the limit.              |
| `MaxSessionsPerClient`       | `8`         | Connections from one address; `0` removes the limit.            |
| `MaxAuthenticationAttempts`  | `3`         | Failed AUTHs before the session is dropped.                     |
| `SessionTimeout`             | `00:02:00`  | How long one session may stay open.                             |
| `CommandWaitTimeout`         | `00:01:00`  | How long to wait for the next command.                          |
| `HealthPort`                 | `8080`      | HTTP health endpoint; `0` disables it.                          |
| `GroupByDate`                | `true`      | Write into a `yyyy-MM-dd` subfolder per day.                    |

The three port lists are the one place the defaults do not live in `appsettings.json`: the
configuration binder appends to array defaults instead of replacing them, so a value there would
be added to whatever you configure rather than superseded by it.

`MaxConcurrentSessions` is what bounds memory: each in-flight message is held whole, up to
`MaxMessageSize`. Connections beyond the limit are dropped until one finishes.
`MaxSessionsPerClient` keeps one host from taking that whole budget and starving everyone else.

## Authentication and TLS

Configuring a credential pair makes AUTH mandatory:

```json
"MailSink": { "Username": "app", "Password": "s3cret" }
```

Or as environment variables: `MailSink__Username=app`, `MailSink__Password=s3cret`. A `Username`
without a `Password`, or the other way round, fails at startup rather than quietly going
unenforced. Outside `Development` the pair is not optional at all.

There is no accept-anything mode and no separate switch to make the credentials bite. With a pair
configured, `MAIL FROM` is answered `530` until the session has authenticated. Without one — only
possible in `Development` — no authenticator is registered at all, so there is nothing that could
accept a wrong password in the first place.

**AUTH is only ever offered across an encrypted connection.** On a STARTTLS port it is absent from
the first `EHLO` and appears in the second, after the upgrade. Together with that `530`, this
means a session which skips STARTTLS has no route to a delivered message, and credentials cannot
cross the wire in the clear even from a client that would have been willing to send them. Nothing
below TLS 1.2 is offered, and `Tls:MinimumProtocol: Tls13` narrows it further.

Three failed AUTHs drop the session (`MaxAuthenticationAttempts`), and each one is logged at
warning with the username and the client address, so a run of them is something a SIEM can alert
on. The password never reaches the log.

### Where the certificate comes from

A Key Vault certificate:

```text
MailSink__Tls__KeyVaultCertificateUri=https://<vault>.vault.azure.net/certificates/mailsink-smtp
```

Leave the version off, as above, and a rotation is picked up within `Tls:RefreshInterval` without
a restart. The private key is read through the secret that backs the certificate — a Key Vault
certificate object is only the public half — so the sink's identity needs both
`Key Vault Certificate User` and `Key Vault Secrets User`. [deploy.ps1](deploy/deploy.ps1) grants
both, and `MailSink:KeyVault:AllowedHosts` constrains which vault the URI may point at.

### Where the password comes from

Never `appsettings.json` — that file is committed. There are two supported places, one per
environment:

| Environment | Source                   | Set up by                               |
| ----------- | ------------------------ | --------------------------------------- |
| Local       | `appsettings.local.json` | You, by hand. Gitignored.               |
| Azure       | Key Vault                | [deploy/deploy.ps1](deploy/deploy.ps1). |

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

## Health

The sink answers `GET /healthz` on `MailSink:HealthPort` (8080 by default) with `200 ok` while it
is listening and `503` once it is not. The port is never published alongside the SMTP ports, so an
orchestrator on the host can reach it and a sender cannot.

[deploy.ps1](deploy/deploy.ps1) wires it to an ACI liveness probe, which is the whole reason it
exists: Azure Container Instances supports only `exec` and `httpGet` probes — there is no TCP
probe — so without it nothing could tell a wedged container from a healthy one.

Set `HealthPort` to `0` to switch it off. If the port is already taken, a `Development` run logs a
warning and carries on; anywhere else the host stops, because a deployment whose probe never
answers would be restarted forever.

## Docker

```powershell
docker compose up -d --build
```

Mail lands in `./mail` on the host. [compose.yaml](compose.yaml) sets
`DOTNET_ENVIRONMENT=Development`, so this is the plain-text, no-credentials mode — it is the local
convenience path, not a deployment. The published port is bound to `127.0.0.1` accordingly:
Docker's port publishing bypasses the host firewall, so binding every interface would hand the
whole LAN an open sink. The container runs as a non-root user.

To run the deployed posture under Docker instead, drop `DOTNET_ENVIRONMENT`, set
`MailSink__Tls__KeyVaultCertificateUri` and the credentials, and publish the TLS ports. Note that
the image listens on `2587` and `2465` rather than `587` and `465`: both of those are privileged,
and the container deliberately does not run as root, so it listens high and you map the port
clients should see — `-p 587:2587`.

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

On the very first deployment the vault role assignments have to propagate before the sink can read
its password and certificate, so the container may restart a couple of times for a minute or two;
the script says so when that applies.

**Certificate.** The script issues a self-signed certificate into the same vault and grants the
container's identity the two roles needed to read it. Its subject is the public FQDN for
`-Exposure Public`, and `<name-prefix>.internal` for Private, where there is no name to use; pass
`-CertificateSubject` to issue it for whatever your senders will actually connect to. Because it
is self-signed, senders have to be told to trust it — the script prints the
`az keyvault certificate download` command for that. Replace the certificate in the vault with one
from your own CA and the sink picks the replacement up on its next refresh.

**Ports.** `-StartTlsPort` defaults to `2587` and `-ImplicitTlsPort` to `2465`, not the standard
`587` and `465`, because the container runs as a non-root user that may not bind a privileged
port and ACI publishes the container's port as-is. Senders therefore need the port in their
configuration. Front the group with a load balancer if you need the standard numbers.

**Exposure.** The default is `-Exposure Private`: the container group sits in a VNet with no public
IP, reachable from that VNet, peered networks, or over VPN. `-Exposure Public -DnsLabel <label>`
gives it an `<label>.<region>.azurecontainer.io` FQDN instead. Every session has to authenticate
over TLS either way, so a public endpoint is no longer an open sink — but it still accepts any
recipient once authenticated, and it will be found and probed by scanners, so prefer Private.

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

A service has no `DOTNET_ENVIRONMENT` set, so it runs as `Production` and needs a certificate and
a credential pair before it will start. It can bind `587` and `465` directly, unlike the
container, so the defaults are right here. The service account needs to reach Key Vault: give the
machine a managed identity, or set `MailSink:KeyVault:ManagedIdentityClientId`.

## Tests

```powershell
dotnet test
```

`tests/MailSink.Tests` (XUnit) covers six layers:

- **`MailNaming`** — pure function, so file naming is asserted directly: the byte budget, surrogate
  pairs, control and format characters, culture-independent dates, path-separator escaping,
  collision suffixes.
- **`MailCapture`** / **`FileMailWriter`** — the capture pipeline against an in-memory
  `IMailWriter` and a `FakeTimeProvider`, so names are deterministic and the writer-failure path is
  exercised; plus that the writer refuses a path resolving outside the mail directory.
- **`SmtpOptionsFactory`** — that the endpoint wiring matches the environment: which ports open,
  which are secure, and that every deployed endpoint requires AUTH, refuses it unencrypted, and
  pins the protocol floor.
- **`FixedCredentialUserAuthenticator`** — the credential comparison on its own, so the wrong-pair
  cases are asserted without a socket.
- **Configuration** — that `appsettings.local.json` overrides `appsettings.json` but not the
  environment, and that only a real `@Microsoft.KeyVault(` value is treated as a reference.
- **End-to-end** — boot the real listener on a free port against a temp folder (`TestSink`), send
  through MailKit, then re-parse the resulting `.eml` with MimeKit. `TlsEndToEndTests` runs the
  sink outside `Development`, so it exercises the deployed wiring: a round trip over each port
  style, AUTH absent from the pre-STARTTLS `EHLO`, a sender that skips STARTTLS getting nowhere,
  and TLS 1.1 refused. Only the certificate is substituted, since a test cannot reach Key Vault.

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
- [SECURITY.md](SECURITY.md) — the posture in each environment, what counts as a vulnerability,
  and how to report one privately.
- [MIT](LICENSE).
