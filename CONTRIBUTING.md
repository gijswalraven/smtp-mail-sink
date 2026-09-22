# Contributing

Thanks for taking a look. This is a small tool with a deliberately small scope, so the most useful
contributions are bug fixes, platform fixes, and tests.

## Building

You need the .NET SDK version in [global.json](global.json) or a later patch of the same band.

```powershell
dotnet build
dotnet test
```

CI runs the tests on both Linux and Windows. File naming depends on
`Path.GetInvalidFileNameChars()`, which returns a very different set on each, so please make sure
anything touching names passes on both.

## Scope

In scope: capturing mail more faithfully, storing it somewhere else, better file naming, fixes to
the deployment scripts, tests.

Out of scope: turning this into a real mail server. It does not relay, does not queue, does not do
delivery, and will not grow a web UI. If you want a sink with a browser interface, MailHog and
Mailpit already exist and are good.

## Style

[.editorconfig](.editorconfig) covers the mechanical parts. Beyond that, the codebase leans on a
couple of habits worth keeping:

- Comments explain *why*, not *what*. If a line needs a comment to say what it does, the line is
  usually the thing to change.
- Behaviour that can be tested without a socket lives behind a seam — `IMailWriter`,
  `IMessageMetadataReader`, `MailNaming`, `SmtpOptionsFactory` — so that the end-to-end tests can
  stay few and the rest can stay fast.
- Match the surrounding code rather than introducing a new idiom.

## Security

Please do not open a public issue for a security problem. [SECURITY.md](SECURITY.md) explains what
counts as one here — the sink is strict outside the `Development` environment and deliberately
relaxed inside it, so which environment a finding applies to is the first thing to establish — and
how to report it privately.

## Pull requests

Keep them focused, explain what problem the change solves, and add a test if the change is
testable. A PR that fixes one thing well is easier to accept than one that fixes five.
