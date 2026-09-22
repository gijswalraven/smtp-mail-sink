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
    /// every other environment must have both a certificate and a credential pair, and refuses
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
        services.AddSingleton<IMailWriter, FileMailWriter>();
        services.AddSingleton<IMessageMetadataReader, MimeMessageMetadataReader>();
        services.AddSingleton<IMailCapture, MailCapture>();

        // SmtpServer resolves this from the application's service provider.
        services.AddSingleton<IMessageStore, EmlMessageStore>();

        // Bound once here purely to decide what to register; the components themselves take
        // IOptions so they still see the bound instance.
        var bound = section.Get<MailSinkOptions>() ?? new MailSinkOptions();
        var isDevelopment = environment.IsDevelopment();
        bound.Validate(isDevelopment);

        // An authenticator is registered only when a credential pair is configured, so that a
        // local run genuinely leaves nothing that accepts credentials rather than only hiding the
        // AUTH advertisement while an accept-anything authenticator stays wired up behind it.
        // Outside Development, Validate has already established that there is a pair.
        if (bound.HasCredentials)
        {
            services.AddSingleton<IUserAuthenticator, FixedCredentialUserAuthenticator>();
        }

        if (!SmtpOptionsFactory.IsPlainText(bound, isDevelopment))
        {
            services.AddSingleton<ICertificateFactory, KeyVaultCertificateFactory>();
        }

        services.AddHostedService<SmtpListenerService>();

        return services;
    }
}
