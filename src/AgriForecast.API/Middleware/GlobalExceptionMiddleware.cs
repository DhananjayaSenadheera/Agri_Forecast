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

    // ISystemErrorLog is a SINGLETON (it self-scopes every DB access), so constructor injection into
    // this pipeline-lifetime middleware is safe — no scoped service is captured.
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
            // A validation failure is a 400 (client error) — logged as a warning WITHOUT leaking the
            // messages, and NEVER recorded to the SystemErrors table (that table is for 500s only).
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
    // defensively anyway so error logging can NEVER change the response being produced.
    private async Task RecordSystemErrorAsync(HttpContext context, Exception exception)
    {
        try
        {
            // PATH ONLY — never context.Request.QueryString (no query strings/headers/body are logged).
            // CancellationToken.None, NOT context.RequestAborted: an errored request often correlates
            // with a client disconnect, and those are exactly the errors most worth keeping. Audit
            // durability is decoupled from the request lifetime (same discipline as UserActivityAudit).
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

        //**Errors by property name
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
