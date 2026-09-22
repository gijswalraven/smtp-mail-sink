# mail sink

A self-hosted SMTP server that accepts mail from your applications and writes every message to
disk as an `.eml` file. Nothing is relayed, so no mail ever reaches a real mailbox.

Double-click an `.eml` to open it in Outlook, Thunderbird, or Windows Mail — attachments, HTML
body and headers included.

The sink behaves differently depending on the environment it runs in, and this is the one thing
to understand before anything else:

|              | `Development`        | Anywhere else                                 |
| ------------ | -------------------- | --------------------------------------------- |
| Transport    | plain text           | plain text                                    |
| Credentials  | optional             | **required** — it will not start without them |
| Mail at rest | unencrypted          | unencrypted                                   |

Running it locally therefore costs no configuration, and deploying it cannot accidentally reuse
that convenience: outside `Development` a missing credential pair stops the host at startup.

> [!WARNING]
> **This sink does not do TLS.** Every connection is plain text, so the password each sender
> authenticates with, and the mail itself, cross the network in the clear and can be read or
> altered by anything on the path. Put it only where that traffic is already trusted — a private
> network, or a host reachable only from the senders you intend. [SECURITY.md](SECURITY.md) has
> the reasoning.

> [!WARNING]
> **Captured mail is stored unencrypted, in both modes.**
>
> The sink protects the connection and the credentials, not the mailbox. Anything it captures is
> readable by anyone who can reach the host or the storage behind it, so treat the mail directory —
> and the blob container behind it — as the boundary that actually matters. In Azure that boundary
> is an Entra ID role on the container, which is the reason there is no storage account key. It
> accepts any recipient, and it cannot be used as an open relay, because nothing is ever forwarded.
>
> See [SECURITY.md](SECURITY.md) for what is deliberate and what is worth reporting.

## Run it

```powershell
dotnet run --project src/MailSink
```

`dotnet run` puts the host in the `Development` environment, so the listener binds to
`127.0.0.1:1025` in plain text, takes mail from anyone, and writes to
`src/MailSink/mail/<yyyy-MM-dd>/`.

Loopback is the default precisely because nothing here is encrypted and, locally, usually
unauthenticated. An
unencrypted listener will not bind an address other machines can reach unless
`MailSink:AllowPlainTextFromAnyAddress` is also set — a container needs that, because `0.0.0.0` is
the only address reachable inside one, and [compose.yaml](compose.yaml) sets both while publishing
the port on the host's loopback.

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
| Host    | the sink's host name or IP                                       |
| Port    | whatever `Port` is set to; `1025` by default                     |
| SSL/TLS | **off** — the sink does not offer STARTTLS or implicit TLS       |
| Auth    | required, and sent in clear text                                 |

In ASP.NET Core, locally and then deployed:

```json
"Smtp": { "Host": "localhost", "Port": 1025, "EnableSsl": false }
"Smtp": { "Host": "mailsink.example.test", "Port": 2587, "EnableSsl": false }
```

`EnableSsl` stays off in both: a client that insists on STARTTLS will not get it. The tests here
use [MailKit](https://github.com/jstedfast/MailKit) with `SecureSocketOptions.None`.

## File names

```text
mail/orders/2026-09-21/100137-883_gijs@example.test_Order-1234-confirmed.eml
      │      │          │          │                 └── subject, slugged and capped at 60 bytes
      │      │          │          └── first envelope recipient
      │      │          └── time received (HHmmss-fff)
      │      └── date received
      └── the account that delivered it, when more than one client uses the sink
```

A subject that cannot be parsed is simply left out of the name; the message is still stored byte
for byte. Path separators, reserved characters, and control and format characters are replaced
with `-`, so a crafted subject cannot steer the write or smuggle an escape sequence into a log
line. The cap is counted in UTF-8 bytes, not characters, which keeps a subject of emoji inside the
255-byte limit Linux puts on a file name.

## Configuration

Set in `src/MailSink/appsettings.json`, or override with environment variables using the
`MailSink__` prefix (e.g. `MailSink__Port=25`).

| Key                            | Default      | Meaning                                                                                     |
| ------------------------------ | ------------ | ------------------------------------------------------------------------------------------- |
| `MailDirectory`                | `mail`       | Where `.eml` files go. Relative to the content root. Ignored once `Blob:ServiceUri` is set. |
| `ServerName`                   | `mail-sink`  | Name reported in the SMTP greeting.                                                         |
| `ListenAddress`                | `127.0.0.1`  | Bind address. A container needs `0.0.0.0` to be reachable.                                  |
| `AllowPlainTextFromAnyAddress` | `false`      | Lets an unencrypted listener bind something other than loopback.                            |
| `Port`                         | `0`          | Port to listen on. `0` takes the default, `1025`.                                           |
| `MaxMessageSize`               | `10485760`   | Bytes. Larger messages are rejected with 552.                                               |
| `Username`                     | *(empty)*    | Single client. Required outside `Development` unless `Accounts` is set.                     |
| `Password`                     | *(empty)*    | Password for `Username`. Required once it is set.                                           |
| `Accounts:<name>:Username`     | *(none)*     | Several clients. One credential pair and one folder per account.                            |
| `Accounts:<name>:Password`     | *(none)*     | Password for that account. Required.                                                        |
| `Accounts:<name>:Folder`       | *(the name)* | Folder — or blob container — its mail goes to.                                              |
| `Blob:ServiceUri`              | *(empty)*    | Write to this Azure storage account instead of the filesystem.                              |
| `Blob:Container`               | `mail`       | Container for mail that belongs to no account.                                              |
| `Blob:ManagedIdentityClientId` | *(empty)*    | Which user-assigned identity to authenticate with.                                          |
| `MaxConcurrentSessions`        | `64`         | Connections served at once; `0` removes the limit.                                          |
| `MaxSessionsPerClient`         | `8`          | Connections from one address; `0` removes the limit.                                        |
| `MaxAuthenticationAttempts`    | `3`          | Failed AUTHs before the session is dropped.                                                 |
| `SessionTimeout`               | `00:02:00`   | How long one session may stay open.                                                         |
| `CommandWaitTimeout`           | `00:01:00`   | How long to wait for the next command.                                                      |
| `HealthPort`                   | `8080`       | HTTP health endpoint; `0` disables it.                                                      |
| `GroupByDate`                  | `true`       | Write into a `yyyy-MM-dd` subfolder per day.                                                |
| `Retention:MaxAge`             | `00:00:00`   | Delete `.eml` files older than this; `0` keeps them forever.                                |
| `Retention:SweepInterval`      | `01:00:00`   | How often the destination is swept.                                                         |

The three port lists are the one place the defaults do not live in `appsettings.json`: the
configuration binder appends to array defaults instead of replacing them, so a value there would
be added to whatever you configure rather than superseded by it.

`MaxConcurrentSessions` is what bounds memory: each in-flight message is held whole, up to
`MaxMessageSize`. Connections beyond the limit are dropped until one finishes.
`MaxSessionsPerClient` keeps one host from taking that whole budget and starving everyone else.

## Authentication

Configuring a credential pair makes AUTH mandatory:

```json
"MailSink": { "Username": "app", "Password": "s3cret" }
```

Or as environment variables: `MailSink__Username=app`, `MailSink__Password=s3cret`. A `Username`
without a `Password`, or the other way round, fails at startup rather than quietly going
unenforced. Outside `Development` credentials are not optional at all.

There is no accept-anything mode and no separate switch to make the credentials bite. With
credentials configured, `MAIL FROM` is answered `530` until the session has authenticated.
Without any — only possible in `Development` — no authenticator is registered at all, so there is
nothing that could accept a wrong password in the first place.

### Several clients

Give each one an account, and its mail lands in its own folder -- or, in Azure, its own blob
container:

```json
"MailSink": {
  "Accounts": {
    "orders": { "Username": "orders-app", "Password": "s3cret", "Folder": "orders" },
    "crm":    { "Username": "crm-app",    "Password": "hunter2", "Folder": "crm" }
  }
}
```

The key names the account in the startup log and in the vault secrets the deploy script creates.
`Folder` may be left out, and then it is the key; set it explicitly when the folder and the
account should not share a name. An empty `Folder` writes to the root of the mail directory,
which is what a sink that predates accounts already does — so moving an existing `Username` and
`Password` into an account with `"Folder": ""` changes nothing on disk.

`Folder` is also the name of the blob container the account writes to once `Blob:ServiceUri` is
set: one setting, so the two destinations cannot be configured to disagree. Container names are
stricter than folder names, and the sink says which rule a name broke rather than reshaping it —
see [Azure](#azure-container-instances).

As environment variables, one per line, keyed by the same name:

```text
MailSink__Accounts__orders__Username=orders-app
MailSink__Accounts__orders__Password=s3cret
MailSink__Accounts__orders__Folder=orders
```

Configuring both `Accounts` and the flat `Username`/`Password` pair is a startup error; so are two
accounts sharing a username, an account without a password, and a `Folder` that is not a plain
directory name — anything with a path separator in it, a Windows device name like `con`, a leading
or trailing space — or, when mail goes to blob storage, one that is not a legal container name. A folder comes from configuration rather than off the wire, so it is refused
rather than cleaned up: storing mail somewhere other than the name an operator wrote would be
worse than not starting.

On a filesystem, folders separate output but are not an access boundary: nothing reads mail back
out over SMTP, but anyone who can reach the mail directory sees every account's folder. In blob
storage they are a boundary, because each account's folder is a container of its own and a
container is what an Entra ID role can be scoped to.

Each AUTH attempt is compared against every configured account, with no early exit, so how long a
rejection takes does not say which usernames exist.

**AUTH crosses the wire in clear text.** There is no encrypted connection to wait for, so
credentials are offered on the unencrypted one and can be read by anything on the path. That is
the trade this sink makes in exchange for having no certificate to manage. The guard that remains
is reach: the listener refuses to bind anything other than loopback unless
`AllowPlainTextFromAnyAddress` says so in as many words, so exposing it to a network is a
deliberate act rather than a default.

Three failed AUTHs drop the session (`MaxAuthenticationAttempts`), and each one is logged at
warning with the username and the client address, so a run of them is something a SIEM can alert
on. The password never reaches the log.

### Where the password comes from

Passwords are stored as readable secrets, not as hashes — deliberately, because the same password
has to be handed to whoever configures the sending application, and a hash cannot be read back out
for that. What protects them is where they are kept, so keep them out of anything committed.
[SECURITY.md](SECURITY.md) has the reasoning.

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

With accounts, that is one secret and one reference per account, named after it:

```text
MailSink__Accounts__orders__Password=@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/mailsink-smtp-password-orders)
```

`Folder` is not a secret and goes on the container group directly, which makes where an account's
mail lands readable straight off `az container show`.

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

To run the deployed posture under Docker instead, drop `DOTNET_ENVIRONMENT`, set the credentials
and `MailSink__AllowPlainTextFromAnyAddress`, and publish the port senders should see.

## Azure (Container Instances)

App Service can't host this — its front ends only accept inbound traffic on 80/443, so an SMTP
listener is unreachable there. ACI gives you a raw TCP port, and blob storage keeps the `.eml`
files when the container restarts.

```powershell
./deploy/deploy.ps1
```

`-ResourceGroup` defaults to `rg-mailsink` and `-Location` to `westeurope`. To point it at your own
target without passing them every time, set `MAILSINK_RESOURCE_GROUP` and `MAILSINK_LOCATION` in
your shell profile — the script reads both.

Because a bare `./deploy.ps1` would otherwise create resources in whichever subscription the az CLI
happens to be pointed at, it prints the resolved subscription and asks to continue. Naming a
`-Subscription` or `-ResourceGroup` explicitly skips the prompt. `-Force` skips it too, for
unattended runs, but then requires `-Subscription`: skipping the confirmation and leaving the
target implicit are each defensible, and together they deploy a mail sink somewhere nobody chose.

That creates a Basic container registry, builds the image server-side with `az acr build`, creates
a storage account with a blob container per account, creates a key vault holding the SMTP
credentials, and runs the container group. Re-running it targets the same resources — names carry a
hash of the subscription and resource group.

**Credentials.** `-SmtpUsername` defaults to `mailsink`. `-SmtpPassword` is generated on the first
run (32 random alphanumeric characters) and kept in the vault; later runs reuse it rather than
silently rotating the credential every configured sender depends on. Pass `-RotatePassword` when
you do want a new one. The script prints how to read it back:

```powershell
az keyvault secret show --vault-name <vault> -n mailsink-smtp-password --query value -o tsv
```

**Several clients.** Name them with `-Accounts` instead, and each gets a username, a generated
password and a folder on the share:

```powershell
./deploy/deploy.ps1 -Accounts orders, crm
```

The name is the username, the folder and the suffix of both vault secrets
(`mailsink-smtp-password-orders`), so it is held to letters, digits and hyphens. Every run must
name every account: the container group is deployed with exactly the accounts listed, so leaving
one out removes its credentials from the sink — the mail it already delivered stays on the share.
`-RotatePassword` rotates all of them.

The container receives a vault reference, not the secret, and resolves it through a user-assigned
managed identity granted `Key Vault Secrets User`. Nothing sensitive reaches a command line, the
container group's ARM definition, or `az container show`. The generated password reaches
`az keyvault secret set` through a temp file that is deleted immediately, because `--value` would
put it on a command line.

On the very first deployment the vault role assignments have to propagate before the sink can read
its password, so the container may restart a couple of times for a minute or two;
the script says so when that applies.

**Ports.** `-Port` defaults to `2587`, not the standard `587`. The image runs as a non-root user,
and whether such a user may bind a privileged
port depends on the runtime: Docker sets `net.ipv4.ip_unprivileged_port_start=0` and allows it,
Azure Container Instances does not. Both were tested — on ACI the standard ports fail with
`SocketException (13): Permission denied` and the container restarts forever. Senders therefore
need the port in their configuration; put a load balancer in front if they cannot.

**Exposure.** The default is `-Exposure Private`: the container group sits in a VNet with no public
IP, reachable from that VNet, peered networks, or over VPN. `-Exposure Public -DnsLabel <label>`
gives it an `<label>.<region>.azurecontainer.io` FQDN instead. Every session has to authenticate
either way, but the connection is never encrypted, so a public endpoint puts those credentials on
the open internet in clear text. It will also be found and probed by scanners. Prefer Private.

**Where the mail goes, and no storage key.** Captured mail goes to blob storage, written by the
container group as its own user-assigned managed identity, granted `Storage Blob Data Contributor`
on the containers it writes to and nothing else. The storage account is created with **shared key
access disabled**, so the account key stops being a credential that can be leaked, pasted into
someone's script, or rotated across every holder at once. `MailSink__Blob__ServiceUri` is all the
container group is given, and it is not a secret: there is no key and no SAS token in its
definition to read back out of `az container show`.

**A container per account.** Each account writes to a container named after it — `orders`, `crm` —
and the date stays a blob name prefix, so a message lands at
`orders/2026-09-21/100137-883_….eml`. The single-client `-SmtpUsername` shape writes to `mail`.

A container, rather than a folder in one shared container, because a container is the smallest
scope an Entra ID role assignment takes. That is the difference between separation the sink
arranges and separation the storage account enforces: `Storage Blob Data Reader` on `orders` reads
the orders mail and nothing else, and it is the only way to express that without
[ABAC conditions](https://learn.microsoft.com/azure/role-based-access-control/conditions-overview)
on a prefix.

The price is that an account name has to be a legal container name — 3–63 characters, lower case,
digits and single hyphens — which is stricter than a folder name. The sink refuses to start on one
that is not, naming the rule it broke, and `deploy.ps1` holds `-Accounts` to the same set rather
than quietly mangling a name into shape. A filesystem deployment is unaffected: `Orders` is a
perfectly good folder.

**Reading the mail.** As yourself, which is the point. The script grants whoever runs it
`Storage Blob Data Reader` on every container it created, and `--auth-mode login` makes the CLI use
that instead of a key: every read is a named principal in the storage logs, and access is withdrawn
by removing a role assignment rather than by rotating a key for everyone at once.

```powershell
az storage blob list --account-name <storage> -c orders --auth-mode login --query '[].name' -o tsv
az storage blob download-batch --account-name <storage> -s orders --auth-mode login -d .
```

Storage Explorer and azcopy sign in the same way, and the portal shows a container directly. One
account's mail can be handed to a colleague without handing over the rest — a role assignment, not
a credential:

```powershell
az role assignment create --assignee <them> --role 'Storage Blob Data Reader' --scope <account-id>/blobServices/default/containers/orders
```

**Locking down the network.** With no key, a role assignment is what protects captured mail, and
`-RestrictStorageNetwork` (Private exposure only) adds the network to that: a service endpoint on
the container subnet, only that subnet allowed, default action Deny — so a stolen token has to be
used from inside the VNet as well. You then add your own IP to keep reading the mail; the script
prints the command.

**Coming from the Azure Files version.** Earlier deployments mounted a `mail` file share at
`/mail`. That share is no longer mounted and the mail already on it stays where it is. Azure Files
cannot be reached without the account key, so while the share exists the script leaves shared key
access enabled and warns rather than making that mail unreadable. Copy off what is worth keeping,
delete the share with `az storage share-rm delete`, and re-run: the key is switched off on that
run. The commands are printed for you.

**Image pull.** The script uses a user-assigned managed identity with `AcrPull` (ACI does not
support system-assigned identities for registry pulls). Microsoft's documentation lists a Premium
registry as a prerequisite for that path; if the pull fails against the Basic registry the script
creates, re-run with `-UseAdminCredentials` and it will use the registry admin account instead.
That switch is the one path here with a password in it: it is read into a variable at run time,
reaches ARM inside the generated container-group file, and is dropped when the call returns. The
default managed-identity path has no password at all.

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

A service has no `DOTNET_ENVIRONMENT` set, so it runs as `Production` and needs a credential pair
before it will start. It can bind a privileged port directly, unlike the container. The service
account needs to reach Key Vault: give the machine a managed identity, or set
`MailSink:KeyVault:ManagedIdentityClientId`.

## Tests

```powershell
dotnet test
```

`tests/MailSink.Tests` (XUnit) covers these layers:

- **`MailNaming`** — pure function, so file naming is asserted directly: the byte budget, surrogate
  pairs, control and format characters, culture-independent dates, path-separator escaping,
  collision suffixes.
- **`MailCapture`** / **`FileMailWriter`** — the capture pipeline against an in-memory
  `IMailWriter` and a `FakeTimeProvider`, so names are deterministic and the writer-failure path is
  exercised; plus that the writer refuses a path resolving outside the mail directory, and the
  sweep over a real temp folder: what goes at the age boundary, and that a non-`.eml` file and an
  emptied account folder stay.
- **`BlobMailWriter`** — what can be asserted without a storage account: the endpoint it will and
  will not accept (a container, plain HTTP and a SAS token are all refused), the containers it
  derives from a set of accounts, and that a folder name a filesystem would take but Azure would
  not — `Orders`, `hr`, `orders--eu` — fails at startup once mail goes to blob storage, and is
  still perfectly good when it does not.
- **`SmtpOptionsFactory`** — that the endpoint wiring matches the environment: which ports open,
  which are secure, and that every deployed endpoint requires AUTH, refuses it unencrypted, and
  pins the protocol floor.
- **`AccountUserAuthenticator`** — the credential comparison on its own, so the wrong-pair cases
  are asserted without a socket, including one account's username with another's password.
- **`MailSinkOptions`** — the account rules: duplicate usernames, a missing password, and every
  shape of folder name the filesystem would not take.
- **`MailRetentionService`** — the scheduling, with a `FakeTimeProvider`: a sweep at startup, none
  at all when no age is configured, and a cutoff that moves with the clock rather than one worked
  out once when the host started.
- **Configuration** — that `appsettings.local.json` overrides `appsettings.json` but not the
  environment, and that only a real `@Microsoft.KeyVault(` value is treated as a reference.
- **End-to-end** — boot the real listener on a free port against a temp folder (`TestSink`), send
  through MailKit, then re-parse the resulting `.eml` with MimeKit. `AuthenticationEndToEndTests`
  runs the sink with credentials configured, so a wrong password and an unauthenticated
  `MAIL FROM` are both exercised against the real listener.

The seams that make this possible are `IMailWriter`, `IMailCapture`/`IncomingMessage`,
`IMessageMetadataReader` and `SmtpOptionsFactory`; `EmlMessageStore` is only an adapter from
SmtpServer's types onto them.

## Retention

The sink deletes its own mail. Set `MailSink:Retention:MaxAge` and a background sweeper removes
`.eml` files older than that and drops the folders they leave empty. The default is `0`, which
keeps everything forever — nothing is deleted until you ask for it.

```json
"MailSink": {
  "Retention": {
    "MaxAge": "7.00:00:00",
    "SweepInterval": "01:00:00"
  }
}
```

A sweep runs at startup and then every `SweepInterval`, so a sink that was off over the weekend
clears what expired while it was down rather than waiting an hour first. Age is the last write
time, which is what a directory or container listing shows, rather than the timestamp in the name —
that one can be switched off entirely with `GroupByDate`.

Whoever stores the mail is what expires it, so in Azure the same setting sweeps every container
the sink writes to, with the container group's own identity. Nothing has to be scheduled, no storage key is handed out,
and there is no lifecycle rule to keep in step with this setting:

```powershell
./deploy/deploy.ps1 -RetentionHours 168
```

`-RetentionHours` defaults to `168` — a week — rather than to keeping everything: captured mail is
real mail, and a sink that accumulates it indefinitely is a liability rather than a feature. Pass
`0` to turn the sweeper off and empty the containers yourself.

What the sweeper leaves alone: the mail directory itself, every folder an account writes to (an
empty one means that account has had no mail), and anything that is not a `.eml` file. A message it
cannot delete — one still being written, or a destination that was away for a moment — is logged
and tried again on the next sweep.

## Supply chain

Both projects restore from a committed `packages.lock.json`, and CI restores with `--locked-mode`,
so a build on a runner resolves the same transitive graph a build on your machine did. Regenerate
the lock files with a plain `dotnet restore` whenever a `PackageReference` changes and commit the
result — a locked-mode restore fails if the two disagree, which is the whole point of it.

Beyond the tests, CI gates on:

- **`dotnet list package --vulnerable --include-transitive`** — fails on any advisory against any
  package in the graph, direct or not. The command exits 0 whether or not it finds something, so
  the job reads the report rather than the exit code.
- **[dependency-review-action](https://github.com/actions/dependency-review-action)** — pull
  requests only. Catches a dependency being *added* with a known advisory, at review time, which
  the audit above only catches once the merge has happened.
- **CodeQL** for C#, on the `security-extended` suite. Results land in the repository's code
  scanning alerts.
- **[Trivy](https://github.com/aquasecurity/trivy)** against the image the `image` job builds.
  Fails on HIGH and CRITICAL that have a fix available, and uploads a CycloneDX SBOM of the image
  as a workflow artifact.

The Worker SDK's default content glob puts `packages.lock.json` into the publish output, so it
ships inside the image as well. That is worth leaving alone: it is what gives an image scanner
the full NuGet graph to work from, rather than only what `MailSink.deps.json` names.

The audit, CodeQL and the image scan also run weekly. That is the case a push-triggered build
never sees: an advisory filed against a dependency nobody touched.

`deploy.ps1` attaches a CycloneDX SBOM to the image it pushes, as an OCI referrer on the manifest
rather than a file somewhere — delete the image and the SBOM goes with it:

```powershell
oras discover <registry>.azurecr.io/mail-sink:<tag>
```

ACR tasks have no SBOM switch of their own, so that step shells out to `trivy` and
[`oras`](https://oras.land). Without both on `PATH` the deployment prints what it skipped and
carries on.

## Alternatives

If you want a sink with a web UI to browse captured mail, [Mailpit](https://github.com/axllent/mailpit)
and [MailHog](https://github.com/mailhog/MailHog) do that well. This one deliberately stops at
`.eml` files on disk, so the mail opens in a real mail client and nothing new has to be learned.

## Contributing, security, licence

- [CONTRIBUTING.md](CONTRIBUTING.md) — scope, style, and how to run the tests.
- [SECURITY.md](SECURITY.md) — the posture in each environment, what counts as a vulnerability,
  and how to report one privately.
- [MIT](LICENSE).
