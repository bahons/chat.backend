using System.ComponentModel.DataAnnotations;
using chat.Api.Middleware;

namespace chat.Api.Endpoints;

internal static class RequestValidation
{
    public static RouteHandlerBuilder ValidateBody<T>(this RouteHandlerBuilder route) where T : class =>
        route.AddEndpointFilter((context, next) =>
        {
            EnsureValid(context.Arguments.OfType<T>().Single());
            return next(context);
        });

    public static void EnsureValid(object body)
    {
        if (!Validator.TryValidateObject(body, new ValidationContext(body), null, validateAllProperties: true))
        {
            throw new ApiFailure(StatusCodes.Status400BadRequest);
        }
    }
}
