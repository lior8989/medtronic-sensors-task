using System.Text.Json.Serialization;
using Sensor.WebApi.Composition;
using Sensor.WebApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSensorBackend(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter())); // "Running", not 2.
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Sensor Backend API";
    document.Info.Description = "Live status of the Camera/Pointer sensor backend. The web UI is served at '/'.";
    return Task.CompletedTask;
}));

var app = builder.Build();

// Minimal static UI (wwwroot/index.html) at '/'.
app.UseDefaultFiles();
app.UseStaticFiles();

// OpenAPI document (/openapi/v1.json) and Swagger UI (/swagger). Always on: local, trusted-network exercise.
app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Sensor Backend API v1");
    options.DocumentTitle = "Sensor Backend API";
});

app.MapStatusEndpoints();

app.Run();

/// <summary>Entry point; partial so integration tests can host the app with <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
