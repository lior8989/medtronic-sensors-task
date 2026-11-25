using System.Net.Sockets;

namespace SensorShared;

public static class SimulatorApp
{
    public static async Task<int> RunAsync(SensorId sensor, string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine(SimulatorOptions.Usage(sensor));
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var options = SimulatorOptions.Parse(sensor, args);
            await RunLoopAsync(new SensorSession(sensor), options, shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            Console.Error.WriteLine(SimulatorOptions.Usage(sensor));
            return 1;
        }
        catch (SocketException exception)
        {
            Console.Error.WriteLine($"UDP failure: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Console I/O failure: {exception.Message}");
            return 1;
        }
        finally
        {
            shutdown.Cancel();
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task RunLoopAsync(
        SensorSession session, SimulatorOptions options, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(options.ListenEndpoint);
        using var heartbeats = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        using var measurements = new PeriodicTimer(
            TimeSpan.FromMilliseconds(session.Sensor == SensorId.Camera ? 20 : 100));

        Console.WriteLine($"{session.Sensor}: listening on {udp.Client.LocalEndPoint}, backend {options.BackendEndpoint}");
        PrintStatus(session);
        Console.WriteLine("CLI: error | clear | status | help | quit");
        await SendAsync(session.CreateHeartbeat());

        var receive = udp.ReceiveAsync(cancellationToken).AsTask();
        var heartbeat = heartbeats.WaitForNextTickAsync(cancellationToken).AsTask();
        var measurement = measurements.WaitForNextTickAsync(cancellationToken).AsTask();
        // Console input may block synchronously on a terminal; never block the UDP event loop.
        Task<string?>? console = Task.Run(Console.ReadLine);
        var noConsole = Task.Delay(Timeout.Infinite, cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = await Task.WhenAny(receive, console ?? noConsole, heartbeat, measurement);
            if (completed == receive)
            {
                var datagram = await receive;
                if (!datagram.RemoteEndPoint.Equals(options.BackendEndpoint))
                {
                    Console.Error.WriteLine($"Rejected command from unexpected endpoint {datagram.RemoteEndPoint}.");
                }
                else if (!SensorProtocol.TryDecodeCommand(
                    datagram.Buffer, session.Sensor, session.Id, out var command, out var error))
                {
                    Console.Error.WriteLine($"Rejected command: {error}");
                }
                else if (!session.ApplyCommand(command))
                {
                    Console.Error.WriteLine($"Rejected duplicate/stale command sequence {command.Sequence}.");
                }
                else
                {
                    Console.WriteLine($"UDP {command.Command} accepted (sequence {command.Sequence}).");
                    PrintStatus(session);
                    await SendAsync(session.CreateHeartbeat());
                }

                receive = udp.ReceiveAsync(cancellationToken).AsTask();
            }
            else if (completed == console)
            {
                var line = await console;
                if (line is null)
                {
                    Console.WriteLine("Standard input closed; continuing UDP operation.");
                    console = null;
                    continue;
                }

                switch (line.Trim().ToLowerInvariant())
                {
                    case "error":
                        session.SetError();
                        PrintStatus(session);
                        await SendAsync(session.CreateHeartbeat());
                        break;
                    case "clear":
                        session.ClearError();
                        PrintStatus(session);
                        await SendAsync(session.CreateHeartbeat());
                        break;
                    case "status":
                        PrintStatus(session);
                        break;
                    case "help":
                        Console.WriteLine("error: set Error; clear: clear Error to Setup; status; help; quit");
                        break;
                    case "quit":
                        return;
                    default:
                        Console.Error.WriteLine("Unknown CLI command. Use error, clear, status, help, or quit.");
                        break;
                }

                console = Task.Run(Console.ReadLine);
            }
            else if (completed == heartbeat)
            {
                await heartbeat;
                await SendAsync(session.CreateHeartbeat());
                heartbeat = heartbeats.WaitForNextTickAsync(cancellationToken).AsTask();
            }
            else if (completed == measurement)
            {
                await measurement;
                if (session.State == SensorState.Ready)
                {
                    await SendAsync(session.CreateMeasurement());
                }

                measurement = measurements.WaitForNextTickAsync(cancellationToken).AsTask();
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        async Task SendAsync(byte[] packet)
        {
            try
            {
                await udp.SendAsync(packet, options.BackendEndpoint, cancellationToken);
            }
            catch (SocketException exception)
            {
                Console.Error.WriteLine($"UDP send to {options.BackendEndpoint} failed: {exception.Message}");
            }
        }
    }

    private static void PrintStatus(SensorSession session) =>
        Console.WriteLine(
            $"State={session.State}, session={session.Id:X16}, lastCommand={session.LastCommandSequence}");
}
