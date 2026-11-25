using System.Globalization;
using System.Net;

namespace SensorShared;

public sealed record SimulatorOptions(IPEndPoint ListenEndpoint, IPEndPoint BackendEndpoint)
{
    public static SimulatorOptions Parse(SensorId sensor, string[] args)
    {
        var listenAddress = IPAddress.Loopback;
        var backendAddress = IPAddress.Loopback;
        var listenPort = sensor == SensorId.Camera ? 8100 : 8200;
        var backendPort = 7450;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index += 2)
        {
            var option = args[index];
            if (index + 1 == args.Length || !seen.Add(option))
            {
                throw new ArgumentException($"Missing value or duplicate option: {option}");
            }

            var value = args[index + 1];
            switch (option)
            {
                case "--listen-address":
                    listenAddress = ParseAddress(option, value);
                    break;
                case "--backend-address":
                    backendAddress = ParseAddress(option, value);
                    break;
                case "--listen-port":
                    listenPort = ParsePort(option, value);
                    break;
                case "--backend-port":
                    backendPort = ParsePort(option, value);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        if (listenAddress.AddressFamily != backendAddress.AddressFamily)
        {
            throw new ArgumentException("Listen and backend addresses must use the same IP address family.");
        }

        if (backendAddress.Equals(IPAddress.Any) || backendAddress.Equals(IPAddress.IPv6Any))
        {
            throw new ArgumentException("Backend address must be a destination, not a wildcard address.");
        }

        return new SimulatorOptions(
            new IPEndPoint(listenAddress, listenPort), new IPEndPoint(backendAddress, backendPort));
    }

    public static string Usage(SensorId sensor) => $"""
        {sensor} sensor (.NET 10)
        Options:
          --listen-address IP    Bind address (default 127.0.0.1)
          --listen-port PORT     Command/source port (default {(sensor == SensorId.Camera ? 8100 : 8200)})
          --backend-address IP   Backend address (default 127.0.0.1)
          --backend-port PORT    Backend data/command-source port (default 7450)
          --help                Show this help
        CLI: error | clear | status | help | quit
        Starts in Setup. Only a UDP Start command can enter Ready.
        """;

    private static IPAddress ParseAddress(string option, string value) =>
        IPAddress.TryParse(value, out var address)
            ? address
            : throw new ArgumentException($"{option} requires an IPv4 or IPv6 literal.");

    private static int ParsePort(string option, string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && port is >= 1 and <= 65535
            ? port
            : throw new ArgumentException($"{option} requires a port from 1 to 65535.");
}
