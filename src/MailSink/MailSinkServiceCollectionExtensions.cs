using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmtpServer.Authentication;
using SmtpServer.Storage;

namespace MailSink;

public static class MailSinkServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything the sink needs. Shared by the host and the integration tests so both
    /// exercise the same composition.
    /// </summary>
    public static IServiceCollection AddMailSink(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(MailSinkOptions.SectionName);
        services.Configure<MailSinkOptions>(section);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMailWriter, FileMailWriter>();
        services.AddSingleton<IMessageMetadataReader, MimeMessageMetadataReader>();
        services.AddSingleton<IMailCapture, MailCapture>();

        // SmtpServer resolves this from the application's service provider.
        services.AddSingleton<IMessageStore, EmlMessageStore>();

        // Bound once here purely to decide what to register; the components themselves take
        // IOptions so they still see the bound instance.
        var bound = section.Get<MailSinkOptions>() ?? new MailSinkOptions();
        bound.ValidateCredentials();

        // An authenticator is registered only when one can actually be satisfied, so that turning
        // authentication off genuinely leaves nothing that accepts credentials, rather than only
        // hiding the AUTH advertisement while an accept-anything authenticator stays wired up
        // behind it.
        if (bound.HasFixedCredentials)
        {
            services.AddSingleton<IUserAuthenticator, FixedCredentialUserAuthenticator>();
        }
        else if (bound.AllowAnyCredentials)
        {
            services.AddSingleton<IUserAuthenticator, AcceptAnyUserAuthenticator>();
        }

        services.AddHostedService<SmtpListenerService>();

        return services;
    }
}
