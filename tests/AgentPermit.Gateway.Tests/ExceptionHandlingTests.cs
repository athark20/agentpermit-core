using System.Net;
using System.Text;
using System.Text.Json;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="GlobalExceptionHandler"/>: verifies the single RFC 7807 error contract,
/// that unexpected 500s never leak internals, and that domain exceptions map precisely.
/// The handler is exercised directly against a <see cref="DefaultHttpContext"/> with a real
/// <see cref="IProblemDetailsService"/> resolved from a minimal container.
/// </summary>
public sealed class ExceptionHandlingTests
{
    /// <summary>Builds a minimal container with a fully-usable default ProblemDetails writer.</summary>
    private static ServiceProvider BuildProblemDetailsProvider()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddProblemDetails();
        return services.BuildServiceProvider();
    }

    /// <summary>Runs the handler for the given exception and returns the status, parsed body JSON, and raw body text.</summary>
    private static async Task<(int Status, JsonElement Body, string Raw)> RunAsync(Exception ex)
    {
        var provider = BuildProblemDetailsProvider();

        var problemDetailsService = provider.GetRequiredService<IProblemDetailsService>();
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance, problemDetailsService);

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var bodyStream = new MemoryStream();
        httpContext.Response.Body = bodyStream;
        httpContext.Request.Path = "/api/test";

        var handled = await handler.TryHandleAsync(httpContext, ex, CancellationToken.None);
        Assert.True(handled, "Handler should report the exception was handled.");

        bodyStream.Position = 0;
        var raw = Encoding.UTF8.GetString(bodyStream.ToArray());
        using var doc = JsonDocument.Parse(raw);
        return (httpContext.Response.StatusCode, doc.RootElement.Clone(), raw);
    }

    [Fact]
    public async Task UnexpectedException_Returns500_Generic_WithoutLeakingInternals()
    {
        const string secret = "Object reference not set. Connection string=Server=prod;Password=hunter2";
        var (status, body, raw) = await RunAsync(new InvalidOperationException(secret));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal("UNEXPECTED", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));

        var detail = body.GetProperty("detail").GetString()!;
        Assert.Contains("Something went wrong on our side", detail);
        Assert.Contains("Quote reference", detail);

        // The raw exception message, secret, type name, and stack must NOT be present anywhere.
        Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain("hunter2", raw);
        Assert.DoesNotContain("InvalidOperationException", raw);
        Assert.DoesNotContain("Connection string", raw);
    }

    [Fact]
    public async Task NotFoundException_Returns404_WithCode_AndPreservedDetail()
    {
        const string message = "Agent 'acme-bot' was not found in the registry.";
        var (status, body, _) = await RunAsync(new NotFoundException(message));

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal("NOT_FOUND", body.GetProperty("code").GetString());
        Assert.Equal(message, body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task ValidationException_Returns400_WithCode_AndFieldErrors()
    {
        var errors = new Dictionary<string, string[]>
        {
            ["amount"] = new[] { "Amount must be greater than zero." },
            ["currency"] = new[] { "Currency is required.", "Currency must be ISO 4217." },
        };
        var (status, body, _) = await RunAsync(
            new ValidationException("One or more validation errors occurred.", errors));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("VALIDATION", body.GetProperty("code").GetString());

        var errorsEl = body.GetProperty("errors");
        Assert.Equal("Amount must be greater than zero.", errorsEl.GetProperty("amount")[0].GetString());
        Assert.Equal(2, errorsEl.GetProperty("currency").GetArrayLength());
    }

    [Fact]
    public async Task UnauthorizedAccessException_MapsTo403_Forbidden()
    {
        var (status, body, raw) = await RunAsync(new UnauthorizedAccessException("secret internal reason"));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal("FORBIDDEN", body.GetProperty("code").GetString());
        // The internal message must not leak; a safe generic detail is used instead.
        Assert.DoesNotContain("secret internal reason", raw);
    }

    [Fact]
    public async Task ClientDisconnect_IsNotHandled_AndNotWritten()
    {
        var provider = BuildProblemDetailsProvider();
        var handler = new GlobalExceptionHandler(
            NullLogger<GlobalExceptionHandler>.Instance,
            provider.GetRequiredService<IProblemDetailsService>());

        var cts = new CancellationTokenSource();
        cts.Cancel();
        var httpContext = new DefaultHttpContext { RequestServices = provider, RequestAborted = cts.Token };

        var handled = await handler.TryHandleAsync(
            httpContext, new OperationCanceledException(), CancellationToken.None);

        // Client went away: handler declines so the request is treated as aborted, not a 500.
        Assert.False(handled);
    }

    // ---- End-to-end: converted controller paths emit the typed error contract --------------------

    /// <summary>A converted domain path (config delete of a missing record) yields 404 + NOT_FOUND via the handler.</summary>
    [Fact]
    public async Task AdminConfig_DeleteMissing_Returns404_NotFoundContract()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.DeleteAsync("/api/admin/config/alert-rule/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NOT_FOUND", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    /// <summary>A converted domain path (rollout for an unknown agent) yields 404 + NOT_FOUND via the handler.</summary>
    [Fact]
    public async Task AdminRollout_UnknownAgent_Returns404_NotFoundContract()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/admin/rollout/no-such-agent");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NOT_FOUND", body.GetProperty("code").GetString());
    }

    // ---- Diagnostics endpoint: each kind maps to the right code/status ---------------------------

    [Theory]
    [InlineData("validation", HttpStatusCode.BadRequest, "VALIDATION")]
    [InlineData("not-found", HttpStatusCode.NotFound, "NOT_FOUND")]
    [InlineData("conflict", HttpStatusCode.Conflict, "CONFLICT")]
    [InlineData("licence", HttpStatusCode.PaymentRequired, "LICENCE_REQUIRED")]
    [InlineData("forbidden", HttpStatusCode.Forbidden, "FORBIDDEN")]
    [InlineData("bananas", HttpStatusCode.BadRequest, "VALIDATION")] // unknown kind -> validation
    public async Task Diagnostics_ErrorKind_MapsToContract(string kind, HttpStatusCode expectedStatus, string expectedCode)
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync($"/api/admin/diagnostics/error/{kind}");
        Assert.Equal(expectedStatus, resp.StatusCode);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    /// <summary>The 'unexpected' kind proves the safe-500 path: generic UNEXPECTED, no raw exception text leaks.</summary>
    [Fact]
    public async Task Diagnostics_Unexpected_Returns500_WithoutLeaking()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/admin/diagnostics/error/unexpected");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        var raw = await resp.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("UNEXPECTED", body.GetProperty("code").GetString());
        Assert.DoesNotContain("must NOT see this text", raw);
    }

    [Fact]
    public async Task Diagnostics_Ping_Returns200_Ok()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/admin/diagnostics/ping");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("ok").GetBoolean());
    }

    /// <summary>Diagnostics is admin-gated: without the configured key it returns the auth 403, not a thrown kind.</summary>
    [Fact]
    public async Task Diagnostics_RequiresAdmin_WhenKeyConfigured()
    {
        using var factory = new GatewayFactory { AdminApiKey = "s3cret-admin" };
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/admin/diagnostics/error/validation");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
