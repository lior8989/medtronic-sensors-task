using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sensor.Communication.Protocol;
using Sensor.Communication.Transport;
using Sensor.Control.Processing;
using Sensor.Control.Sensors;
using Sensor.Control.Status;
using Sensor.Control.Supervision;
using Sensor.WebApi.Configuration;
using Sensor.WebApi.Contracts;
using Sensor.WebApi.Hosting;

namespace Sensor.WebApi.Composition;

/// <summary>Composition root of the sensor backend: binds options and registers all services.</summary>
public static class SensorBackendServiceCollectionExtensions
{
    /// <summary>Registers the backend (transport, sensors, supervisor, processing, status, hosted service).</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration with the <c>Sensors</c>, <c>Supervisor</c> and <c>Pairing</c> sections.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddSensorBackend(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SensorNetworkOptions>()
            .Bind(configuration.GetSection(SensorNetworkOptions.SectionName))
            .Validate(o => o.IsValid(), "Sensors endpoints must be IP endpoints such as 127.0.0.1:7450.")
            .ValidateOnStart();

        // The Control library takes plain option objects (no dependency on Microsoft.Extensions.Options).
        services.AddOptions<SupervisorOptions>().Bind(configuration.GetSection("Supervisor"));
        services.AddOptions<PairingOptions>().Bind(configuration.GetSection("Pairing"));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SupervisorOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PairingOptions>>().Value);

        services.TryAddSingleton(TimeProvider.System);

        // One bound socket for receiving and for sending commands (the sensors require that source).
        services.AddSingleton<IDatagramTransport>(sp =>
            new UdpDatagramTransport(IPEndPoint.Parse(sp.GetRequiredService<IOptions<SensorNetworkOptions>>().Value.BackendEndpoint)));

        // Processing: the worker is the queue, the pairer is the supervisor's measurement sink.
        services.AddSingleton<IMeasurementProcessor, PixelLookupProcessor>();
        services.AddSingleton<ProcessingWorker>();
        services.AddSingleton<IProcessingQueue>(sp => sp.GetRequiredService<ProcessingWorker>());
        services.AddSingleton<MeasurementPairer>();
        services.AddSingleton<IMeasurementSink>(sp => sp.GetRequiredService<MeasurementPairer>());

        // Two SensorControl instances of the same type, so they are created explicitly here.
        services.AddSingleton(sp =>
        {
            var network = sp.GetRequiredService<IOptions<SensorNetworkOptions>>().Value;
            var transport = sp.GetRequiredService<IDatagramTransport>();
            var time = sp.GetRequiredService<TimeProvider>();
            var sensorLogger = sp.GetRequiredService<ILogger<SensorControl>>();

            return new SystemSupervisor(
                new SensorControl(SensorId.Camera, IPEndPoint.Parse(network.CameraEndpoint), transport, time, sensorLogger),
                new SensorControl(SensorId.Pointer, IPEndPoint.Parse(network.PointerEndpoint), transport, time, sensorLogger),
                sp.GetRequiredService<IMeasurementSink>(),
                sp.GetRequiredService<SupervisorOptions>(),
                time,
                sp.GetRequiredService<ILogger<SystemSupervisor>>());
        });

        services.AddSingleton<SystemStatusStore>();
        services.AddSingleton<ISupervisorObserver>(sp => sp.GetRequiredService<SystemStatusStore>());
        services.AddSingleton<SupervisorEventLoop>();
        services.AddSingleton<StatusMapper>();

        services.AddHostedService<SensorBackendService>();
        return services;
    }
}
