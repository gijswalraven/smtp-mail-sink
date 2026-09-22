using MailSink;

var builder = Host.CreateApplicationBuilder(args);

// Optional, gitignored overlay for local development: keeps MailSink:Password out of git.
builder.Configuration.AddLocalSettings();

// Run last, so a reference is picked up whichever source carried it. In Azure the SMTP password
// arrives as an environment variable holding a vault URI, not the secret, so nothing sensitive
// reaches the container's command line or its ARM definition.
builder.Configuration.ResolveKeyVaultReferences(
    allowDeveloperCredentials: builder.Environment.IsDevelopment());

builder.Services.AddWindowsService(options => options.ServiceName = "mail sink");
builder.Services.AddMailSink(builder.Configuration, builder.Environment);

builder.Build().Run();
