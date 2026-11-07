using chat.Api.DependencyInjection;
using chat.Api.Endpoints;
using chat.Api.Hubs;
using chat.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
builder.Services.AddBootstrap(builder.Configuration);
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "chat API";
        document.Info.Version = "0.1.0";
        document.Servers?.Clear();
        return Task.CompletedTask;
    });
});

var app = builder.Build();

app.UseMiddleware<RequestContextMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages(context => ApiErrors.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode));

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
else
{
    app.MapOpenApi().AllowAnonymous().ExcludeFromDescription();
}

app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapHub<MessagingHub>(BootstrapServices.MessagingPath, options =>
{
    options.CloseOnAuthenticationExpiration = true;
}).RequireAuthorization(BootstrapServices.SignedInPolicy).ExcludeFromDescription();

app.Run();

public partial class Program;
