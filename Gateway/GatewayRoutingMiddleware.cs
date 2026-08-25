using Gateway.Constants;

namespace Gateway;

public static class GatewayRoutingMiddleware
{
    public static Func<HttpContext, Func<Task>, Task> Create()
    {
        return async (context, _) =>
        {
            var path = context.Request.Path;
            var method = context.Request.Method;
            var query = context.Request.QueryString.HasValue
                ? context.Request.QueryString.Value!
                : string.Empty;

            if (HttpMethods.IsGet(method) && path == GatewayRoutes.Live)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return;
            }

            if (HttpMethods.IsGet(method) && path.StartsWithSegments(GatewayRoutes.OpenApiPrefix))
            {
                await ApiForwarder.ForwardAsync(context, path.Value! + query);
                return;
            }

            if (HttpMethods.IsGet(method) && path == GatewayRoutes.Ready)
            {
                await ApiForwarder.ForwardAsync(context, GatewayRoutes.Ready);
                return;
            }

            var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (HttpMethods.IsPost(method)
                && segments.Length == GatewayRoutes.ApiSegmentsCount
                && segments[0] == GatewayRoutes.ApiPrefix)
            {
                await ApiForwarder.ForwardAsync(context, path.Value! + query);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
        };
    }
}