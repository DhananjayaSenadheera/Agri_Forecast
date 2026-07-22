using System.Text.Json;
using AgriForecast.API.Middleware;
using AgriForecast.Application.Services;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// Tests for GlobalExceptionMiddleware (Logs hub PR A / Phase 3). Load-bearing guarantees: (1) an
/// unhandled exception is recorded to ISystemErrorLog with the request METHOD + PATH ONLY (never the
/// query string) and the trace id, and produces a 500 body carrying traceId plus the UNCHANGED generic
/// Title/Detail (no leaked message/stack); (2) a ValidationException (400 path) records NOTHING to the
/// error log.
/// </summary>
public class GlobalExceptionMiddlewareTests
{
    private sealed class SpyErrorLog : ISystemErrorLog
    {
        public int Calls;
        public Exception? LastEx;
        public string? LastMethod;
        public string? LastPath;
        public string? LastTraceId;

        public Task RecordAsync(Exception ex, string method, string path, string? traceId, CancellationToken ct)
        {
            Calls++;
            LastEx = ex;
            LastMethod = method;
            LastPath = path;
            LastTraceId = traceId;
            return Task.CompletedTask;
        }
    }

    private static DefaultHttpContext BuildContext(string method, string path, string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(queryString);
        context.TraceIdentifier = "trace-abc";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var json = await reader.ReadToEndAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task UnhandledException_Records_MethodAndPathOnly_And500BodyHasTraceId()
    {
        var spy = new SpyErrorLog();
        var context = BuildContext("POST", "/api/forecast/predict", "?secret=leak&token=xyz");

        var middleware = new GlobalExceptionMiddleware(
            _ => throw new InvalidOperationException("internal boom"),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            spy);

        await middleware.Invoke(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        spy.Calls.Should().Be(1);
        spy.LastMethod.Should().Be("POST");
        spy.LastPath.Should().Be("/api/forecast/predict", "the query string must never be captured");
        spy.LastPath.Should().NotContain("secret").And.NotContain("token");
        spy.LastTraceId.Should().Be("trace-abc");
        spy.LastEx!.GetType().Should().Be(typeof(InvalidOperationException));

        var body = await ReadBodyAsync(context);
        body.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
        body.GetProperty("detail").GetString().Should().Be("Please try again later.");
        body.GetProperty("status").GetInt32().Should().Be(500);
        body.GetProperty("traceId").GetString().Should().Be("trace-abc");
        // The generic body must never leak the internal message.
        body.GetRawText().Should().NotContain("internal boom");
    }

    private sealed class ThrowingErrorLog : ISystemErrorLog
    {
        public Task RecordAsync(Exception ex, string method, string path, string? traceId, CancellationToken ct)
            => throw new InvalidOperationException("error log blew up");
    }

    private sealed class TokenCapturingErrorLog : ISystemErrorLog
    {
        public CancellationToken? CapturedToken;

        public Task RecordAsync(Exception ex, string method, string path, string? traceId, CancellationToken ct)
        {
            CapturedToken = ct;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ErrorLogThatThrows_StillProducesTheCleanGeneric500()
    {
        // Belt-and-braces guard: even if the writer violated its never-throws contract, the middleware
        // must still produce the unchanged generic 500 with traceId.
        var context = BuildContext("GET", "/api/forecast", "");

        var middleware = new GlobalExceptionMiddleware(
            _ => throw new InvalidOperationException("internal boom"),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            new ThrowingErrorLog());

        await middleware.Invoke(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var body = await ReadBodyAsync(context);
        body.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
        body.GetProperty("traceId").GetString().Should().Be("trace-abc");
        body.GetRawText().Should().NotContain("internal boom").And.NotContain("error log blew up");
    }

    [Fact]
    public async Task ErrorLogWrite_IsDecoupledFromTheRequestToken()
    {
        // An errored request often correlates with a client disconnect; the write must not be
        // cancellable by the request (CancellationToken.None, same discipline as UserActivityAudit).
        var spy = new TokenCapturingErrorLog();
        var context = BuildContext("GET", "/api/forecast", "");
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        context.RequestAborted = aborted.Token;

        var middleware = new GlobalExceptionMiddleware(
            _ => throw new InvalidOperationException("boom"),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            spy);

        await middleware.Invoke(context);

        (spy.CapturedToken == CancellationToken.None).Should().BeTrue(
            "audit durability must not die with the request connection");
    }

    [Fact]
    public async Task ValidationException_400Path_RecordsNothingToTheErrorLog()
    {
        var spy = new SpyErrorLog();
        var context = BuildContext("GET", "/api/forecast", "");

        var failures = new[] { new ValidationFailure("Field", "is required") };
        var middleware = new GlobalExceptionMiddleware(
            _ => throw new ValidationException(failures),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            spy);

        await middleware.Invoke(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        spy.Calls.Should().Be(0, "a 400 client error is never written to the SystemErrors table");
    }
}
