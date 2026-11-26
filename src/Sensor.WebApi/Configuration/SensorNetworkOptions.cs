using System.Net;

namespace Sensor.WebApi.Configuration;

/// <summary>
/// UDP endpoints (configuration section <c>Sensors</c>). Values are IP-literal endpoints such as
/// <c>127.0.0.1:7450</c> or <c>[::1]:7450</c>; hostnames are not supported (as in the simulators).
/// </summary>
public sealed class SensorNetworkOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Sensors";

    /// <summary>The backend's single UDP socket: receives sensor data and sends commands. Default 127.0.0.1:7450.</summary>
    public string BackendEndpoint { get; set; } = "127.0.0.1:7450";

    /// <summary>The Camera sensor's command endpoint (and the only accepted source of Camera packets). Default 127.0.0.1:8100.</summary>
    public string CameraEndpoint { get; set; } = "127.0.0.1:8100";

    /// <summary>The Pointer sensor's command endpoint (and the only accepted source of Pointer packets). Default 127.0.0.1:8200.</summary>
    public string PointerEndpoint { get; set; } = "127.0.0.1:8200";

    /// <summary>True when all three values parse as IP endpoints.</summary>
    /// <returns>Whether the options are valid.</returns>
    public bool IsValid() =>
        IPEndPoint.TryParse(BackendEndpoint, out _)
        && IPEndPoint.TryParse(CameraEndpoint, out _)
        && IPEndPoint.TryParse(PointerEndpoint, out _);
}
