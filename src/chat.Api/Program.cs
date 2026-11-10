using chat.Api.Businesses;
using chat.Api.DependencyInjection;
using chat.Api.Endpoints;
using chat.Api.Hubs;
using chat.Api.Identity;
using chat.Api.Messaging;
using chat.Api.Middleware;
using chat.Application.Push;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var seedAdmin = args.SequenceEqual(["--seed-admin"]);
var migrate = args.SequenceEqual(["--migrate"]);
var builder = WebApplication.CreateBuilder(seedAdmin || migrate ? [] : args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
builder.Services.AddBootstrap(builder.Configuration);
if (builder.Environment.IsProduction() || builder.Configuration.GetValue<bool>("Runtime:EnforceSingleReplica"))
{
    builder.Services.AddHostedService<SingleReplicaGuard>();
}
builder.Services.AddAccountIdentity();
builder.Services.AddScoped<BusinessDrafts>();
builder.Services.AddScoped<BusinessService>();
builder.Services.AddScoped<ConversationAccess>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<DeviceRegistry>();
builder.Services.AddConversationMedia(builder.Environment.IsDevelopment());
builder.Services.AddSingleton<RealtimeConnections>();
builder.Services.AddScoped<RealtimeAccess>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<RealtimeRevocationWorker>();
builder.Services.AddOptions<PushOptions>().BindConfiguration("Push");
builder.Services.AddHttpClient<IPushGateway, ExpoPushGateway>(client =>
{
    client.BaseAddress = new Uri("https://exp.host/--/api/v2/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHostedService<PushWorker>();
builder.Services.AddHostedService<AttachmentCleanupWorker>();
builder.Services.AddOptions<RetentionOptions>().BindConfiguration("Retention")
    .Validate(options => options.ExpiredSessionsDays is >= 7 and <= 365 && options.CompletedWorkDays is >= 7 and <= 365
        && options.UnattachedMediaHours is >= 24 and <= 720
        && options.SweepIntervalSeconds is >= 1 and <= 600
        && options.MaxBatchesPerSweep is >= 1 and <= 1000
        && options.SweepBudgetSeconds is >= 1 and <= 60, "Invalid retention configuration.")
    .ValidateOnStart();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddHostedService<WorkerBacklogMonitor>();
builder.Services.AddHostedService<ExpiredDraftWorker>();
builder.Services.AddHostedService<VerificationDocumentScanner>();
builder.Services.AddOpenApi("v1", ApiDocumentation.Configure);

var app = builder.Build();
if (migrate)
{
    await using (app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
        Console.WriteLine("Database migrations applied.");
    }
    return;
}
if (seedAdmin)
{
    await using (app)
    {
        await AdminSeed.RunAsync(app);
    }

    return;
}

app.UseMiddleware<RequestContextMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages(context => ApiErrors.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode));

app.UseProductionTransport();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous().ExcludeFromDescription();
}

app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapAccountEndpoints();
app.MapBusinessEndpoints();
app.MapDirectoryEndpoints();
app.MapConversationEndpoints();
app.MapMediaEndpoints();
app.MapDeviceEndpoints();
app.MapHub<MessagingHub>(BootstrapServices.MessagingPath, options =>
{
    options.CloseOnAuthenticationExpiration = true;
}).RequireAuthorization(AuthPrincipal.ParticipantPolicy).ExcludeFromDescription();

app.Run();

public partial class Program;
