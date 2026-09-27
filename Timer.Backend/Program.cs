using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Backend.Contracts;
using Timer.Backend.Administration;
using Timer.Backend.Configuration;
using Timer.Backend.Endpoints;
using Timer.Backend.Infrastructure;
using Timer.Backend.WriteApi;
using Timer.RequestManager.Backend;

var administrativeInvocation = BackendAdministrativeCli.Parse(args);
var builder = WebApplication.CreateBuilder(administrativeInvocation.ConfigurationArguments);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();

var backendOptions = TimerBackendOptions.FromConfiguration(builder.Configuration);

if (administrativeInvocation.Operation == BackendAdministrativeOperation.Migrate)
{
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
    using var storage = TimerBackendStorageFactory.Create(
        backendOptions.DatabaseType, backendOptions.ConnectionString, loggerFactory,
        enableOutboxWorker: false);
    storage.MigrateMasterDatabase();
    return;
}

if (administrativeInvocation.Operation == BackendAdministrativeOperation.ConvertRunDates)
{
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
    using var storage = TimerBackendStorageFactory.Create(
        backendOptions.DatabaseType, backendOptions.ConnectionString, loggerFactory,
        enableOutboxWorker: false);
    storage.ConvertMasterRunDates(administrativeInvocation.BackupConfirmed);
    return;
}

var writeApiOptions = TimerWriteApiOptions.FromConfiguration(builder.Configuration);
var runtimeOptions = TimerBackendRuntimeOptions.FromConfiguration(builder.Configuration);
if (administrativeInvocation.Operation is BackendAdministrativeOperation.SetTier
    or BackendAdministrativeOperation.RecalculateScores)
{
    // Fail before connecting: these commands write the configured factors onto every board.
    BackendAdministrativeCli.EnsureExplicitScorePolicy(writeApiOptions);
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
    using var storage = TimerBackendStorageFactory.Create(
        backendOptions.DatabaseType, backendOptions.ConnectionString, loggerFactory,
        enableOutboxWorker: false);
    storage.Start(initializeSchema: false, allowReadRepair: false, requireWriteSchema: true);
    var result = BackendAdministrativeCli.ExecuteAsync(administrativeInvocation, storage, writeApiOptions)
        .GetAwaiter().GetResult();
    if (!result.MapFound)
        throw new InvalidOperationException($"Map '{administrativeInvocation.MapName}' was not found.");
    Console.WriteLine(BackendAdministrativeCli.FormatCompletion(administrativeInvocation, result));
    return;
}

builder.Services.AddSingleton(backendOptions);
builder.Services.AddSingleton(writeApiOptions);
TimerBackendRuntimeRegistration.Add(builder.Services, runtimeOptions);
builder.Services.AddSingleton<TimerBackendStorage>(services =>
    TimerBackendStorageFactory.Create(
        backendOptions.DatabaseType,
        backendOptions.ConnectionString,
        services.GetRequiredService<ILoggerFactory>(),
        backendOptions.ShouldRunOutboxWorker(writeApiOptions.Enabled)));
builder.Services.AddHostedService<TimerBackendStorageHostedService>();
builder.Services.AddResponseCompression();
builder.Services.ConfigureHttpJsonOptions(BackendJsonOptions.Configure);
TimerWriteApiRegistration.Add(builder.Services, writeApiOptions);

var app = builder.Build();
if (writeApiOptions.Enabled && writeApiOptions.LocalPorts.Count == 0)
{
    app.Logger.LogWarning(
        "The unauthenticated write API is served on every Kestrel listener. Set {Setting} to the gRPC listener's port to restrict it.",
        $"{TimerWriteApiOptions.SectionName}:LocalPorts");
}

app.UseResponseCompression();
app.UseExceptionHandler(exceptionApplication => exceptionApplication.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new ApiErrorDto
    {
        Code = "internal_error",
        Message = "The request could not be completed.",
        RequestId = context.TraceIdentifier,
    }, BackendJsonContext.Default.ApiErrorDto, cancellationToken: context.RequestAborted);
}));
app.UseRouting();
app.UseRequestTimeouts();
app.UseOutputCache();
TimerReadEndpoints.Map(app);
TimerWriteApiRegistration.Map(app, writeApiOptions);
app.Run();

public partial class Program;
