using System.ComponentModel.DataAnnotations;
using chat.Domain.Identity;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Identity;

internal static class AdminSeed
{
    public static async Task RunAsync(WebApplication app)
    {
        var email = app.Configuration["AdminSeed:Email"];
        var password = app.Configuration["AdminSeed:Password"];
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || !new EmailAddressAttribute().IsValid(email)
            || string.IsNullOrWhiteSpace(password) || password.Length is < 15 or > 128)
        {
            throw new InvalidOperationException("Configure AdminSeed__Email and a 15–128 character AdminSeed__Password.");
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var credentials = scope.ServiceProvider.GetRequiredService<AccountCredentials>();
        var user = credentials.CreateUser(email, password, "Platform administrator", UserKind.PLATFORM_ADMIN);
        user.EmailVerifiedAt = DateTimeOffset.UtcNow;
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException exception) when (AccountService.IsEmailConflict(exception))
        {
            throw new InvalidOperationException("An account already exists with the supplied email.");
        }

        Console.WriteLine("Platform administrator created.");
    }
}
