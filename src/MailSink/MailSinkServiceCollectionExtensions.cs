using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Storage;

namespace MailSink;

public static class MailSinkServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything the sink needs. Shared by the host and the integration tests so both
    /// exercise the same composition.
    /// </summary>
    /// <param name="environment">
    /// Decides how strict the sink is. Development may run in plain text with no credentials;
    /// every other environment must have a credential pair, and refuses
    /// to start without them.
    /// </param>
    public static IServiceCollection AddMailSink(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var section = configuration.GetSection(MailSinkOptions.SectionName);
        services.Configure<MailSinkOptions>(section);
        services.Configure<KeyVaultOptions>(configuration.GetSection(KeyVaultOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SinkHealth>();
        services.AddSingleton<IMessageMetadataReader, MimeMessageMetadataReader>();
        services.AddSingleton<IMailCapture, MailCapture>();

        // SmtpServer resolves this from the application's service provider.
        services.AddSingleton<IMessageStore, EmlMessageStore>();

        // Bound once here purely to decide what to register; the components themselves take
        // IOptions so they still see the bound instance.
        var bound = section.Get<MailSinkOptions>() ?? new MailSinkOptions();
        var isDevelopment = environment.IsDevelopment();
        bound.Validate(isDevelopment);

        // A container named in configuration is what decides where mail goes, so that a local run
        // and a Windows service keep writing files with no Azure dependency at all: the blob
        // writer is not constructed, and no credential chain is built looking for an identity that
        // is not there.
        if (bound.Blob.IsConfigured)
        {
            services.AddSingleton<IMailWriter, BlobMailWriter>();
        }
        else
        {
            services.AddSingleton<IMailWriter, FileMailWriter>();
        }

        // An authenticator is registered only when an account is configured, so that a local run
        // genuinely leaves nothing that accepts credentials rather than only hiding the AUTH
        // advertisement while an accept-anything authenticator stays wired up behind it. Outside
        // Development, Validate has already established that there is at least one account.
        if (bound.HasCredentials)
        {
            services.AddSingleton<IUserAuthenticator, AccountUserAuthenticator>();
        }

        services.AddHostedService<SmtpListenerService>();
        services.AddHostedService<HealthEndpointService>();
        services.AddHostedService<MailRetentionService>();

        return services;
    }
}
