using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sensor.Communication.Protocol;
using Sensor.Communication.Transport;

namespace Sensor.Tests.Integration;

/// <summary>
/// The whole backend (real host, real UDP socket on an OS-assigned port) wired to two
/// <see cref="SimulatedSensor"/>s on loopback. Observed only through the public HTTP API.
/// </summary>
internal sealed class SensorSystem : IAsyncDisposable
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(8);

    private readonly WebApplicationFactory<Program> _factory;
    private IPEndPoint? _backend;

    public SensorSystem()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Sensors:BackendEndpoint", "127.0.0.1:0");
            builder.UseSetting("Sensors:CameraEndpoint", Camera.Endpoint.ToString());
            builder.UseSetting("Sensors:PointerEndpoint", Pointer.Endpoint.ToString());
        });
        Client = _factory.CreateClient(); // Starts the host (and the backend's UDP socket).
    }

    public SimulatedSensor Camera { get; } = new(SensorId.Camera);

    public SimulatedSensor Pointer { get; } = new(SensorId.Pointer);

    public HttpClient Client { get; }

    /// <summary>The backend's bound UDP endpoint (port assigned by the OS).</summary>
    public IPEndPoint Backend => _backend ??= _factory.Services.GetRequiredService<IDatagramTransport>().LocalEndPoint;

    public void StartSensors()
    {
        Camera.Start(Backend);
        Pointer.Start(Backend);
    }

    public async Task<JsonElement> GetStatusAsync()
    {
        var json = await Client.GetStringAsync(new Uri("/api/status", UriKind.Relative), TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Polls the status until <paramref name="condition"/> holds; fails with the last status after the timeout.</summary>
    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> condition, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout ?? DefaultWait);
        var status = await GetStatusAsync();
        while (!condition(status))
        {
            if (cts.IsCancellationRequested)
            {
                Assert.Fail($"Condition not met in time. Last status: {status}");
            }

            await Task.Delay(20, CancellationToken.None);
            status = await GetStatusAsync();
        }

        return status;
    }

    public Task<JsonElement> WaitForStateAsync(string systemState, TimeSpan? timeout = null) =>
        WaitForAsync(s => s.GetProperty("systemState").GetString() == systemState, timeout);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        await Camera.DisposeAsync();
        await Pointer.DisposeAsync();
    }
}
