using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sensor.Tests.WebApi;

/// <summary>
/// Hosts the real application in-memory for endpoint tests. The backend's UDP socket binds to an
/// OS-assigned port (0), so tests never collide with a running backend on 7450.
/// </summary>
public sealed class SensorApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Sensors:BackendEndpoint", "127.0.0.1:0");
        builder.UseEnvironment("Testing");
    }
}
