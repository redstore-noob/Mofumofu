using Mofumofu.RemoteProtocol;

namespace Mofumofu.ServerAgent.Security;

public sealed class RequestSecurityMiddleware
{
    private const string AuthenticationItemKey = "Mofumofu.ServerAgent.Authentication";
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestSecurityMiddleware> _logger;

    public RequestSecurityMiddleware(RequestDelegate next, ILogger<RequestSecurityMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, AgentOptions options, TokenRegistry registry,
        TokenRateLimiter rateLimiter)
    {
        try
        {
            AgentSecurityMetadata? security = context.GetEndpoint()?.Metadata
                .GetMetadata<AgentSecurityMetadata>();
            if (security is not null)
            {
                string routeInstance = context.Request.RouteValues["instanceId"]?.ToString() ?? string.Empty;
                if (!string.Equals(routeInstance, options.InstanceId, StringComparison.Ordinal))
                {
                    throw ApiException.NotFound("The server instance was not found.");
                }

                if (HttpMethods.IsGet(context.Request.Method) || context.WebSockets.IsWebSocketRequest)
                {
                    ProtocolRequest.ValidateQueryVersion(context);
                }

                AuthenticationContext authentication = registry.Authenticate(
                    context.Request.Headers.Authorization.ToString(), options.InstanceId);
                if (security.Root && !authentication.IsRoot)
                {
                    throw new ApiException(StatusCodes.Status403Forbidden, ProtocolErrors.Forbidden,
                        "This endpoint requires a root token.");
                }

                if (!security.Root && security.Scope.Length > 0 &&
                    !authentication.Scopes.Contains(security.Scope))
                {
                    throw new ApiException(StatusCodes.Status403Forbidden, ProtocolErrors.InsufficientScope,
                        $"The '{security.Scope}' scope is required.");
                }

                rateLimiter.Check(authentication.TokenId, security.Category);
                context.Items[AuthenticationItemKey] = authentication;
            }

            await _next(context);
            if (!context.Response.HasStarted && context.Response.StatusCode == StatusCodes.Status404NotFound)
            {
                await WriteError(context, ApiException.NotFound());
            }
        }
        catch (ApiException error)
        {
            if (!context.Response.HasStarted)
            {
                await WriteError(context, error);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _logger.LogError("Unhandled Agent request exception of type {ExceptionType}.", error.GetType().FullName);
            if (!context.Response.HasStarted)
            {
                await WriteError(context, new ApiException(StatusCodes.Status500InternalServerError,
                    ProtocolErrors.InternalError, "The request could not be completed."));
            }
        }
    }

    public static AuthenticationContext Authentication(HttpContext context) =>
        context.Items.TryGetValue(AuthenticationItemKey, out object? value) && value is AuthenticationContext auth
            ? auth
            : throw new ApiException(StatusCodes.Status401Unauthorized, ProtocolErrors.Unauthorized,
                "Authentication is required.");

    private static Task WriteError(HttpContext context, ApiException error)
    {
        context.Response.Clear();
        context.Response.StatusCode = error.StatusCode;
        if (error.StatusCode == StatusCodes.Status401Unauthorized)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
        }

        return ProtocolRequest.Error(error, context.TraceIdentifier).ExecuteAsync(context);
    }
}
