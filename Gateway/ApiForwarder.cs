namespace Gateway;

public static class ApiForwarder
{
    private static readonly string[] _forwardedRequestHeaders = 
        ["Authorization", "Idempotency-Key", "X-Action-Version"];
    
    public static async Task ForwardAsync(HttpContext context, string targetPathAndQuery)
    {
        var client = context.RequestServices
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("api");
        
        var request = BuildRequest(context, targetPathAndQuery);
        
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);

            await ForwardResponseAsync(response, context);
        }
        catch (HttpRequestException)
        {
            await WriteErrorAsync(context, "dependency.unavailable", "db unavailable", true, StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(context, "action.timeout", "timeout", true, StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task WriteErrorAsync(
        HttpContext context, string code, string message, bool retryable, int status)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new
        {
            status = "error",
            code,
            message,
            retryable,
            details = new { },
            meta = new { correlationId = Guid.NewGuid().ToString(), actionVersion = (int?)null }
        });
    }

    private static HttpRequestMessage BuildRequest(HttpContext context, string targetPathAndQuery)
    {
        var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method), targetPathAndQuery);

        foreach (var headerName in _forwardedRequestHeaders)
        {
            if (context.Request.Headers.TryGetValue(headerName, out var values))
                request.Headers.TryAddWithoutValidation(headerName, values.ToArray());
        }

        if (context.Request.ContentLength > 0 || context.Request.ContentType is not null)
        {
            var content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is not null)
            {
                content.Headers.TryAddWithoutValidation(
                    "Content-Type", context.Request.ContentType);
            }

            request.Content = content;
        }
        
        return request;
    }

    private static Task ForwardResponseAsync(HttpResponseMessage apiResponse, HttpContext context)
    {
        context.Response.StatusCode = (int)apiResponse.StatusCode;

        if (apiResponse.Content.Headers.ContentType is not null)
        {
            context.Response.ContentType =
                apiResponse.Content.Headers.ContentType.ToString();
        }

        if (apiResponse.Content.Headers.ContentLength is not null)
        {
            context.Response.ContentLength = apiResponse.Content.Headers.ContentLength;
        }

        return apiResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}