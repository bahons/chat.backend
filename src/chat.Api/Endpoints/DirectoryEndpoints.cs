using chat.Api.Identity;
using chat.Api.Middleware;
using chat.Contracts.Businesses;
using chat.Domain.Businesses;
using chat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace chat.Api.Endpoints;

internal static class DirectoryEndpoints
{
    public static void MapDirectoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var directory = endpoints.MapGroup("/v1/businesses").WithTags("Directory")
            .RequireAuthorization(AuthPrincipal.ClientPolicy);
        directory.MapGet("", async (string? q, int? limit, string? cursor, ChatDbContext db, CancellationToken ct) =>
        {
            var page = CursorPage.Parse(limit, cursor);
            var query = VisibleBusinesses(db);
            if (!string.IsNullOrWhiteSpace(q))
            {
                q = q.Trim();
                if (q.Length is < 3 or > 100)
                {
                    throw new ApiFailure(StatusCodes.Status400BadRequest);
                }

                var pattern = "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
                query = query.Where(business => EF.Functions.ILike(business.DisplayName, pattern, "\\")
                    || EF.Functions.ILike(business.Category, pattern, "\\"));
            }

            if (page.Before is { } before)
            {
                query = query.Where(business => EF.Functions.LessThan(ValueTuple.Create(business.CreatedAt, business.Id),
                    ValueTuple.Create(before, page.BeforeId)));
            }

            var rows = await query.OrderByDescending(business => business.CreatedAt).ThenByDescending(business => business.Id)
                .Select(business => new { business.Id, business.CreatedAt, business.DisplayName, business.Category, business.Description })
                .Take(page.Limit + 1).ToListAsync(ct);
            return TypedResults.Ok(page.MakePage(rows, row => new BusinessCard(row.Id, row.DisplayName, row.Category, row.Description),
                row => row.CreatedAt, row => row.Id));
        }).WithName("SearchBusinesses");

        directory.MapGet("/{id:guid}", async (Guid id, ChatDbContext db, CancellationToken ct) =>
        {
            var card = await VisibleBusinesses(db).Where(business => business.Id == id)
                .Select(business => new BusinessCard(business.Id, business.DisplayName, business.Category, business.Description))
                .SingleOrDefaultAsync(ct) ?? throw new ApiFailure(StatusCodes.Status404NotFound);
            return TypedResults.Ok(card);
        }).WithName("GetBusiness").WithApiErrors(404);
    }

    private static IQueryable<Business> VisibleBusinesses(ChatDbContext db) =>
        db.Businesses.AsNoTracking().Where(business => business.Status == BusinessStatus.VERIFIED && business.IsActive);
}
