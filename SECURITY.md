# Security policy

## What this project is

mail sink is a **development and test tool**. It runs an SMTP server that accepts every message
offered to it and writes it to disk. By design it:

- accepts mail without authentication,
- accepts any username and password when `AllowAnyCredentials` is on,
- accepts any recipient address,
- speaks plain SMTP with no TLS,
- stores every message unencrypted on disk, headers and body included.

None of the above is a vulnerability. They are the product. Reports that amount to "it accepts any
password" or "the traffic is not encrypted" will be closed with a pointer to this page.

It follows that the sink belongs on a trusted network — a developer machine, a CI runner, a private
VNet — and never on a public endpoint you care about. Anything it captures should be treated as
readable by anyone who can reach the host or the storage behind it.

## What is a vulnerability

Report it if you find a way to:

- escape the configured mail directory and read or write elsewhere on the host, for example through
  a crafted subject, envelope address, or header,
- execute code in the sink process by sending it a message,
- crash or wedge the process with a single message or a small number of connections, beyond the
  configured `MaxMessageSize` and `MaxConcurrentSessions` limits,
- make `FixedCredentialUserAuthenticator` accept a credential pair it was not configured with,
- recover a configured password from a log, a file name, or a stored message,
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
