using System.Security.Claims;
using Ppki.Application;

namespace Ppki.Api;

public sealed class InternalAdminEndpointFilter(IInternalAdminAuthorizationService authorization) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var claim = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var actorUserId) || actorUserId == Guid.Empty)
            return Results.Unauthorized();

        try
        {
            await authorization.RequirePpkiAdminAsync(actorUserId, context.HttpContext.RequestAborted);
        }
        catch (InternalAdminAuthorizationException)
        {
            return Results.Forbid();
        }

        return await next(context);
    }
}
