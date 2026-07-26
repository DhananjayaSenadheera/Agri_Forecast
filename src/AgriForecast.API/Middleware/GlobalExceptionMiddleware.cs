using System.Net;
using AgriForecast.Application.Services;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Middleware;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly ISystemErrorLog _systemErrorLog;

    // ISystemErrorLog is a singleton that self-scopes every DB access, so constructor-injecting it into this
    // pipeline-lifetime middleware captures no scoped service.
    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        ISystemErrorLog systemErrorLog)
    {
        _next = next;
        _logger = logger;
        _systemErrorLog = systemErrorLog;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException ex)
        {
            // A validation failure is a client error: log a warning without the messages, and never record it
            // to the SystemErrors table, which is for 500s only.
            _logger.LogWarning(ex, "Validation exception");
            await HandleValidationExceptionAsync(context, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            await RecordSystemErrorAsync(context, ex);
            await HandleGeneralExceptionAsync(context, ex);
        }
    }

    // Fire-and-await the fire-safe error-log writer. The writer itself can never throw, but wrap it
    // defensively anyway so error logging can never change the response being produced.
    private async Task RecordSystemErrorAsync(HttpContext context, Exception exception)
    {
        try
        {
            // PATH ONLY — never the query string, and no headers or body are logged either.
            // CancellationToken.None, not context.RequestAborted: an errored request often correlates with a
            // client disconnect, and those are exactly the errors most worth keeping.
            await _systemErrorLog.RecordAsync(
                exception,
                context.Request.Method,
                context.Request.Path.Value ?? string.Empty,
                context.TraceIdentifier,
                CancellationToken.None);
        }
        catch
        {
            // Unreachable by contract; belt-and-braces so an error-log failure can never break the 500.
        }
    }

    private async Task HandleGeneralExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        context.Response.ContentType = "application/json";

        var problemDetails = new ProblemDetails
        {
            Title = "An unexpected error occurred.",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "Please try again later.",
            Instance = context.Request.Path
        };
        problemDetails.Extensions["traceId"] = context.TraceIdentifier;

        await context.Response.WriteAsJsonAsync(problemDetails);
    }

    private static async Task HandleValidationExceptionAsync(HttpContext context, ValidationException exception)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;

        var errors= exception.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray());

        var problemDetails = new ValidationProblemDetails(errors)
        {
            Title = "One or more validation errors occurred.",
            Status = StatusCodes.Status400BadRequest,
            Detail = "Please refer to the errors property for additional details.",
            Instance = context.Request.Path
        };

        await context.Response.WriteAsJsonAsync(problemDetails);

    }
}
