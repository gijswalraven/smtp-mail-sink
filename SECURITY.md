# Security policy

## What this project is

mail sink is an SMTP server that accepts mail and writes it to disk as `.eml` files, so you can
see exactly what an application sent. It is built to run in a deployed environment, not only on a
developer's machine.

Outside the `Development` environment it refuses to start unless it has a credential pair and a
TLS certificate, and it then refuses:

- any session that has not authenticated, and
- any session that is not encrypted with TLS 1.2 or better.

AUTH is never advertised across an unencrypted connection, so credentials cannot cross the wire in
the clear even from a client that would have been willing to send them.

What the sink still does by design:

- accepts any recipient address — it is a sink, not a router, and nothing is ever forwarded,
- stores every message unencrypted on disk, headers and body included,
- in the `Development` environment **only**, listens in plain text and, unless credentials are
  configured, accepts mail from anyone. This is what keeps a local run zero-configuration.

That last point is a deliberate, bounded exception. `deploy.ps1` never sets `DOTNET_ENVIRONMENT`,
so a deployed container runs as `Production` and gets the strict rules; and a plain-text port
configured outside `Development` is a startup error rather than a quiet downgrade.

Per-account folders are not an access boundary either. They keep one client's mail out of
another's listing, and nothing reads mail back out over SMTP, but anyone who can reach the share
can read all of it.

The unencrypted store is the part to plan around. Anything the sink captures is readable by anyone
who can reach the host or the storage behind it — the transport and the credentials are protected,
the mail at rest is not. Treat the mail directory, and the Azure Files share behind it, as the
boundary that actually protects captured mail.

Reports that amount to "the Development mode accepts any password" or "stored messages are not
encrypted" will be closed with a pointer to this page.

## What is a vulnerability

Report it if you find a way to:

- deliver a message without authenticating, or across an unencrypted connection, in any
  environment other than `Development`,
- downgrade or strip the TLS: negotiate below TLS 1.2, get `AUTH` advertised or accepted before
  STARTTLS has completed, or get the sink to present a certificate it was not configured with,
- make `AccountUserAuthenticator` accept a credential pair it was not configured with -- one
  account's username with another's password included -- or learn from the timing or the content
  of a rejection which usernames exist,
- have a message filed under an account other than the one the session authenticated as, or get a
  configured folder name to resolve outside the mail directory,
- get past `MaxAuthenticationAttempts`, `MaxConcurrentSessions` or `MaxSessionsPerClient`, or
  otherwise make one client deny the sink to others,
- escape the configured mail directory and read or write elsewhere on the host, for example
  through a crafted subject, envelope address, or header,
- execute code in the sink process by sending it a message,
- crash or wedge the process with a single message or a small number of connections, beyond the
  configured `MaxMessageSize` and session limits,
- recover a configured password, or the certificate's private key, from a log, a file name, or a
  stored message,
- obtain the Azure storage key from anything the deployment scripts do.

## Reporting

Please use GitHub's **private vulnerability reporting** on this repository
(Security → Report a vulnerability). That keeps the report private until a fix exists.

Do not open a public issue for something in the list above.

Include what you sent, what you expected, and what happened. A `.eml` or a short script that
reproduces it is ideal. Please do not include real credentials or real captured mail.

## Expectations

This is a personal project maintained in spare time. I will acknowledge a report as soon as I can,
and I would rather hear about something small than not hear about it. There is no bounty.

## Supported versions

Only the current `main` branch. There are no maintained release branches.
