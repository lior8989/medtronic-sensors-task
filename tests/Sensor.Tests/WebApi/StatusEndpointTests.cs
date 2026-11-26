using System.Net;
using System.Text.Json;

namespace Sensor.Tests.WebApi;

/// <summary>Endpoint tests against the in-memory host (no sensors running: the system stays Initializing).</summary>
public sealed class StatusEndpointTests(SensorApiFactory factory) : IClassFixture<SensorApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetStatus_NoSensors_ReturnsInitializingWithStringEnums()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/status", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = json.RootElement;
        Assert.Equal("Initializing", root.GetProperty("systemState").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("faultReason").ValueKind);
        Assert.Equal("NeverSeen", root.GetProperty("camera").GetProperty("connection").GetString());
        Assert.Equal("NeverSeen", root.GetProperty("pointer").GetProperty("connection").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("latestResult").ValueKind);
        Assert.Equal(0, root.GetProperty("processing").GetProperty("pairsSubmitted").GetInt64());
    }

    [Fact]
    public async Task GetEvents_StreamsStatusEventImmediately()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        Assert.Equal("event: status", await reader.ReadLineAsync(Ct));
        var data = await reader.ReadLineAsync(Ct);
        Assert.StartsWith("data: ", data, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(data!["data: ".Length..]);
        Assert.Equal("Initializing", json.RootElement.GetProperty("systemState").GetString());
    }

    [Fact]
    public async Task GetRoot_ServesWebUi()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative), Ct);

        Assert.Contains("Sensor Backend", html, StringComparison.Ordinal);
        Assert.Contains("app.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApiDocument_DescribesBothEndpoints()
    {
        using var client = factory.CreateClient();

        var document = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);

        Assert.Contains("/api/status", document, StringComparison.Ordinal);
        Assert.Contains("/api/events", document, StringComparison.Ordinal);
        Assert.Contains("Current system status", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwaggerUi_IsServed()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/swagger/index.html", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
