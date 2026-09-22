using System.Net.Http;
using MailSink;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailSink.Tests;

/// <summary>
/// The endpoint an orchestrator probes. Azure Container Instances offers no TCP probe, so this is
/// the only thing standing between a wedged container and one that looks fine forever.
/// </summary>
public class HealthEndpointTests
{
    [Fact]
    public async Task Reports_healthy_while_the_sink_is_listening()
    {
        var healthPort = TestSink.FreePort();
        await using var sink = await TestSink.StartAsync(new Dictionary<string, string?>
        {
            ["MailSink:HealthPort"] = healthPort.ToString(),
        });

        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{healthPort}{HealthEndpointService.Path}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Answers_404_on_any_other_path()
    {
        var healthPort = TestSink.FreePort();
        await using var sink = await TestSink.StartAsync(new Dictionary<string, string?>
        {
            ["MailSink:HealthPort"] = healthPort.ToString(),
        });

        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{healthPort}/");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_port_of_zero_turns_the_endpoint_off_without_affecting_the_sink()
    {
        await using var sink = await TestSink.StartAsync(new Dictionary<string, string?>
        {
            ["MailSink:HealthPort"] = "0",
        });

        using var message = new System.Net.Mail.MailMessage(
            "app@example.test", "gijs@example.test", "No health endpoint", "body");

        await sink.SendAsync(message);

        Assert.NotNull(await sink.WaitForSingleFileAsync());
    }

    [Theory]
    [InlineData("GET /healthz HTTP/1.1", true, "200 OK")]
    [InlineData("GET /healthz/ HTTP/1.1", true, "200 OK")]
    [InlineData("GET /healthz?x=1 HTTP/1.1", true, "200 OK")]
    [InlineData("GET /healthz HTTP/1.1", false, "503 Service Unavailable")]
    [InlineData("GET / HTTP/1.1", true, "404 Not Found")]
    [InlineData("GET /../secret HTTP/1.1", true, "404 Not Found")]
    [InlineData("POST /healthz HTTP/1.1", true, "404 Not Found")]
    [InlineData("", true, "404 Not Found")]
    public void Answers_according_to_the_request_and_the_listener_state(
        string requestLine, bool listening, string expectedStatus)
    {
        var health = new SinkHealth();
        if (listening)
        {
            health.MarkListening();
        }

        var service = new HealthEndpointService(
            TestOptions.For(),
            health,
            new HealthTestEnvironment(),
            NullLogger<HealthEndpointService>.Instance);

        Assert.StartsWith($"HTTP/1.1 {expectedStatus}\r\n", service.Response(requestLine));
    }

    [Fact]
    public void Reports_unhealthy_once_the_listener_stops()
    {
        var health = new SinkHealth();
        health.MarkListening();
        Assert.True(health.IsListening);

        health.MarkStopped();

        Assert.False(health.IsListening);
    }

    private sealed class HealthTestEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "MailSink.Tests";

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
