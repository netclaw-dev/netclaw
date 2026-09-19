// -----------------------------------------------------------------------
// <copyright file="TeamsActivityBodyGuardExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Http.Features;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Applies the local Teams ingress body ceiling before the SDK reads a body.
/// </summary>
internal static class TeamsActivityBodyGuardExtensions
{
    public static IApplicationBuilder UseTeamsActivityBodyGuard(this IApplicationBuilder app)
    {
        app.Use(GuardAsync);
        return app;
    }

    private static async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method)
            || !string.Equals(request.Path, TeamsActivityEndpointExtensions.ActivityPath, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (request.ContentLength is > TeamsActivityEndpointExtensions.MaxActivityBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
            bodySizeFeature.MaxRequestBodySize = TeamsActivityEndpointExtensions.MaxActivityBodyBytes;

        await using var bufferedBody = new MemoryStream();
        var buffer = new byte[8192];
        var totalBytes = 0;

        while (totalBytes <= TeamsActivityEndpointExtensions.MaxActivityBodyBytes)
        {
            var allowedBytes = Math.Min(
                buffer.Length,
                TeamsActivityEndpointExtensions.MaxActivityBodyBytes + 1 - totalBytes);
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, allowedBytes), context.RequestAborted);
            if (read == 0)
                break;

            await bufferedBody.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            totalBytes += read;
        }

        if (totalBytes == 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (totalBytes > TeamsActivityEndpointExtensions.MaxActivityBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        request.ContentLength = totalBytes;
        request.Body = new MemoryStream(bufferedBody.ToArray(), writable: false);
        await next(context);
    }
}
