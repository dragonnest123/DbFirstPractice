namespace Gateway.Constants;

public static class GatewayRoutes
{
    public const string Live = "/health/live";
    public const string Ready = "/health/ready";
    public const string OpenApiPrefix = "/openapi";
    public const string ApiPrefix = "api";
    public const int ApiSegmentsCount = 3;
}
