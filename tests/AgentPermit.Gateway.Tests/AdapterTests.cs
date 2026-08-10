using System.Net;
using System.Text;
using System.Text.Json;
using AgentPermit.Gateway.Adapters;
using AgentPermit.Gateway.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPermit.Gateway.Tests;

/// <summary>Resolver + the registry-driven REST and DELEGATED adapters.</summary>
public sealed class AdapterTests
{
    private sealed class StubAdapter(string code) : ActionAdapterBase
    {
        public override string AdapterCode => code;
        public override Task<AdapterResult> ExecuteAsync(AdapterRequest r, CancellationToken ct)
            => Task.FromResult(AdapterResult.Ok("X", "{}"));
    }

    private sealed class StubRegistry(TargetSystemRecord t) : ITargetSystemRegistry
    {
        public TargetSystemRecord? Find(string? code)
            => string.Equals(code, t.TargetSystemCode, StringComparison.OrdinalIgnoreCase) ? t : null;
        public IReadOnlyList<TargetSystemRecord> All() => new[] { t };
        public void Invalidate() { }
    }

    private sealed class StubHandler(Func<int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responder(++Calls));
    }

    private const string Target = "TESTTARGET";

    private static AdapterRequest Req()
    {
        using var doc = JsonDocument.Parse("{\"amount\":100}");
        return new AdapterRequest
        {
            ExecutionId = Guid.NewGuid(), ActionRequestId = Guid.NewGuid(),
            ActionId = "Refund.Issue", TargetSystem = Target, Parameters = doc.RootElement.Clone()
        };
    }

    private static TargetSystemRecord Tgt(string adapterType) => new()
    {
        TargetSystemCode = Target, AdapterType = adapterType, BaseUrl = "http://target",
        ExecutePath = "/run", TimeoutSeconds = 5, MaxRetries = 2, Enabled = true
    };

    private static HttpResponseMessage Resp(HttpStatusCode code, string body = "{}")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void Resolver_ResolvesByCode_AndThrowsOnUnknown()
    {
        var resolver = new ActionAdapterResolver(new IActionAdapter[] { new StubAdapter("A"), new StubAdapter("B") });
        Assert.Equal("A", resolver.Resolve("a").AdapterCode); // case-insensitive
        Assert.True(resolver.TryResolve("B", out _));
        Assert.False(resolver.TryResolve("NOPE", out _));
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve("NOPE"));
    }

    [Fact]
    public async Task Rest_Success_UsesRegistryEndpoint_ReturnsTargetId()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.OK, "{\"id\":\"RES-1\"}"));
        var adapter = new RestActionAdapter(new HttpClient(handler), new StubRegistry(Tgt("REST")), NullLogger<RestActionAdapter>.Instance);
        var result = await adapter.ExecuteAsync(Req(), CancellationToken.None);
        Assert.Equal(AdapterExecutionStatus.Succeeded, result.Status);
        Assert.Equal("RES-1", result.TargetResourceId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Rest_TransientThenSuccess_Retries()
    {
        var handler = new StubHandler(n => n == 1 ? Resp(HttpStatusCode.ServiceUnavailable) : Resp(HttpStatusCode.OK, "{\"id\":\"R2\"}"));
        var adapter = new RestActionAdapter(new HttpClient(handler), new StubRegistry(Tgt("REST")), NullLogger<RestActionAdapter>.Instance);
        var result = await adapter.ExecuteAsync(Req(), CancellationToken.None);
        Assert.Equal(AdapterExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Rest_ClientError_DoesNotRetry()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.BadRequest, "bad"));
        var adapter = new RestActionAdapter(new HttpClient(handler), new StubRegistry(Tgt("REST")), NullLogger<RestActionAdapter>.Instance);
        var result = await adapter.ExecuteAsync(Req(), CancellationToken.None);
        Assert.Equal(AdapterExecutionStatus.Failed, result.Status);
        Assert.Equal("HTTP_400", result.ErrorCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Rest_ServerError_ExhaustsRetries()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.InternalServerError));
        var adapter = new RestActionAdapter(new HttpClient(handler), new StubRegistry(Tgt("REST")), NullLogger<RestActionAdapter>.Instance);
        var result = await adapter.ExecuteAsync(Req(), CancellationToken.None);
        Assert.Equal(AdapterExecutionStatus.Failed, result.Status);
        Assert.Equal(3, handler.Calls); // initial + 2 retries
    }

    [Fact]
    public async Task Rest_UnconfiguredTarget_Fails()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.OK));
        var noBaseUrl = new TargetSystemRecord { TargetSystemCode = Target, AdapterType = "REST", Enabled = true };
        var adapter = new RestActionAdapter(new HttpClient(handler), new StubRegistry(noBaseUrl), NullLogger<RestActionAdapter>.Instance);
        var result = await adapter.ExecuteAsync(Req(), CancellationToken.None);
        Assert.Equal("TARGET_NOT_CONFIGURED", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    // Delegated adapter posts to the flow URL; a flow-reported failure (200 + status:Failed) is honoured.
    [Fact]
    public async Task Delegated_PostsToFlow_AndHonoursReportedFailure()
    {
        var ok = new StubHandler(_ => Resp(HttpStatusCode.OK, "{\"status\":\"Succeeded\",\"id\":\"SN-42\"}"));
        var okAdapter = new DelegatedActionAdapter(new HttpClient(ok), new StubRegistry(Tgt("DELEGATED")), NullLogger<DelegatedActionAdapter>.Instance);
        var okResult = await okAdapter.ExecuteAsync(Req(), CancellationToken.None);
        Assert.Equal(AdapterExecutionStatus.Succeeded, okResult.Status);
        Assert.Equal("SN-42", okResult.TargetResourceId);

        var failed = new StubHandler(_ => Resp(HttpStatusCode.OK, "{\"status\":\"Failed\",\"error\":\"connector rejected\"}"));
        var failAdapter = new DelegatedActionAdapter(new HttpClient(failed), new StubRegistry(Tgt("DELEGATED")), NullLogger<DelegatedActionAdapter>.Instance);
        var failResult = await failAdapter.ExecuteAsync(Req(), CancellationToken.None);
        Assert.Equal(AdapterExecutionStatus.Failed, failResult.Status);
        Assert.Equal("TARGET_REPORTED_FAILURE", failResult.ErrorCode);
    }
}
