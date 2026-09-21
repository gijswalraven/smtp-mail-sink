using MailSink;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace MailSink.Tests;

public class LocalSettingsConfigurationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mail-sink-tests", Guid.NewGuid().ToString("n"));

    public LocalSettingsConfigurationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteFile(string name, string json) =>
        File.WriteAllText(Path.Combine(_directory, name), json);

    private IConfigurationBuilder Builder() =>
        new ConfigurationBuilder()
            .SetBasePath(_directory)
            .AddJsonFile("appsettings.json", optional: true);

    [Fact]
    public void A_missing_local_file_is_not_an_error()
    {
        WriteFile("appsettings.json", """{ "MailSink": { "Username": "from-json" } }""");

        var configuration = Builder().AddLocalSettings().Build();

        Assert.Equal("from-json", configuration["MailSink:Username"]);
    }

    [Fact]
    public void The_local_file_overrides_appsettings()
    {
        WriteFile("appsettings.json", """{ "MailSink": { "Username": "from-json", "Password": "" } }""");
        WriteFile(LocalSettingsConfigurationExtensions.FileName,
            """{ "MailSink": { "Password": "from-local" } }""");

        var configuration = Builder().AddLocalSettings().Build();

        Assert.Equal("from-json", configuration["MailSink:Username"]);
        Assert.Equal("from-local", configuration["MailSink:Password"]);
    }

    [Fact]
    public void Environment_variables_still_win_over_the_local_file()
    {
        // The whole point of inserting rather than appending: a container's MailSink__Password
        // must not be shadowed by a local file that happened to ship with the image.
        const string key = "MailSink__Password";
        WriteFile(LocalSettingsConfigurationExtensions.FileName,
            """{ "MailSink": { "Password": "from-local" } }""");

        Environment.SetEnvironmentVariable(key, "from-environment");
        try
        {
            var configuration = Builder().AddEnvironmentVariables().AddLocalSettings().Build();

            Assert.Equal("from-environment", configuration["MailSink:Password"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void The_local_file_goes_after_the_last_json_source()
    {
        var sources = new List<IConfigurationSource>
        {
            new JsonConfigurationSource { Path = "appsettings.json" },
            new JsonConfigurationSource { Path = "appsettings.Development.json" },
            new Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource(),
        };

        Assert.Equal(2, LocalSettingsConfigurationExtensions.IndexAfterLastJsonSource(sources));
    }

    [Fact]
    public void The_local_file_goes_first_when_there_is_no_json_source()
    {
        var sources = new List<IConfigurationSource>
        {
            new Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource(),
        };

        Assert.Equal(0, LocalSettingsConfigurationExtensions.IndexAfterLastJsonSource(sources));
    }
}
