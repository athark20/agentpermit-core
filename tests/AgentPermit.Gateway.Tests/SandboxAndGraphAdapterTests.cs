using System.Net;
using System.Text;
using System.Text.Json;
using AgentPermit.Gateway.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// The SANDBOX adapter (stands in for target systems with no real downstream yet) and the GRAPH adapter
/// (SharePoint / OneDrive sharing links through Microsoft Graph, with its confidentiality guardrail).
/// </summary>
public sealed class SandboxAndGraphAdapterTests
{
    // ================================================================ sandbox

    [Fact]
    public async Task Sandbox_RecordsTheAction_AndSaysNothingRealWasCalled()
    {
        var adapter = new SandboxActionAdapter(NullLogger<SandboxActionAdapter>.Instance);
        var request = Req("Ticket.Create", "ITSM", new { title = "Printer on fire" });

        var result = await adapter.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AdapterExecutionStatus.Succeeded, result.Status);
        Assert.Equal("SBX-" + request.ActionRequestId.ToString("N")[..12].ToUpperInvariant(), result.TargetResourceId);
        using var doc = JsonDocument.Parse(result.ResultJson!);
        Assert.True(doc.RootElement.GetProperty("sandbox").GetBoolean());
        Assert.Equal("Ticket.Create", doc.RootElement.GetProperty("actionId").GetString());
        Assert.Equal("ITSM", doc.RootElement.GetProperty("targetSystem").GetString());
        Assert.Contains("no real downstream system", doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void Sandbox_ResolvesForTheSandboxAdapterType()
    {
        var resolver = new ActionAdapterResolver(new IActionAdapter[] { new SandboxActionAdapter(NullLogger<SandboxActionAdapter>.Instance) });
        Assert.Equal("SANDBOX", resolver.Resolve("sandbox").AdapterCode);
    }

    // ================================================================ graph

    [Fact]
    public async Task Graph_NotConfigured_FailsClearly_WithoutCallingAnything()
    {
        var graph = new FakeGraph();
        var adapter = Adapter(graph, new GraphOptions());

        var result = await adapter.ExecuteAsync(Req("Document.Share", "SHAREPOINT", new { itemId = "I1" }), CancellationToken.None);

        Assert.Equal("TARGET_NOT_CONFIGURED", result.ErrorCode);
        Assert.Equal(0, graph.Calls);
    }

    [Fact]
    public async Task Graph_UnsupportedScope_IsRejected()
    {
        var adapter = Adapter(new FakeGraph(), Configured());
        var result = await adapter.ExecuteAsync(Req("Document.Share", "SHAREPOINT", new { itemId = "I1", scope = "public" }), CancellationToken.None);
        Assert.Equal("INVALID_PARAMETERS", result.ErrorCode);
    }

    [Fact]
    public async Task Graph_NothingToIdentifyTheFile_IsRejected()
    {
        var adapter = Adapter(new FakeGraph(), Configured());
        var result = await adapter.ExecuteAsync(Req("Document.Share", "SHAREPOINT", new { scope = "organization" }), CancellationToken.None);
        Assert.Equal("INVALID_PARAMETERS", result.ErrorCode);
    }

    [Fact]
    public async Task Graph_CreatesAnOrganizationLink_ByItemId()
    {
        var graph = new FakeGraph();
        var adapter = Adapter(graph, Configured());

        var result = await adapter.ExecuteAsync(
            Req("Document.Share", "SHAREPOINT", new { itemId = "ITEM-7", driveId = "DRIVE-A", linkType = "edit" }), CancellationToken.None);

        Assert.Equal(AdapterExecutionStatus.Succeeded, result.Status);
        Assert.Equal("LINK-1", result.TargetResourceId);
        Assert.EndsWith("/drives/DRIVE-A/items/ITEM-7/createLink", graph.CreateLinkUri);
        Assert.Equal("edit", graph.CreateLinkBody!.GetProperty("type").GetString());
        Assert.Equal("organization", graph.CreateLinkBody.Value.GetProperty("scope").GetString());
        Assert.Equal("Bearer tok-1", graph.CreateLinkAuthorization);
    }

    [Fact]
    public async Task Graph_FindsTheFileByName_InTheDefaultDrive()
    {
        var graph = new FakeGraph { PathLookupId = "ITEM-BY-PATH" };
        var adapter = Adapter(graph, Configured());

        var result = await adapter.ExecuteAsync(Req("Document.Share", "SHAREPOINT", new { fileName = "Q3 plan.docx" }), CancellationToken.None);

        Assert.Equal(AdapterExecutionStatus.Succeeded, result.Status);
        Assert.Contains("/drives/DEFAULT-DRIVE/root:/Q3%20plan.docx?$select=id", graph.LookupUris);
        Assert.EndsWith("/drives/DEFAULT-DRIVE/items/ITEM-BY-PATH/createLink", graph.CreateLinkUri);
    }

    [Fact]
    public async Task Graph_FallsBackToSearch_WhenThePathLookupMisses()
    {
        var graph = new FakeGraph { SearchHitId = "ITEM-BY-SEARCH" };
        var adapter = Adapter(graph, Configured());

        var result = await adapter.ExecuteAsync(Req("Document.Share", "SHAREPOINT", new { fileName = "plan.docx" }), CancellationToken.None);

        Assert.Equal(AdapterExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, graph.LookupUris.Count);
        Assert.EndsWith("/items/ITEM-BY-SEARCH/createLink", graph.CreateLinkUri);
    }

    [Fact]
    public async Task Graph_UnknownFileName_IsReportedByTheTarget()
    {
        var graph = new FakeGraph();
        var adapter = Adapter(graph, Configured());

        var result = await adapter.ExecuteAsync(Req("Document.Share", "SHAREPOINT", new { fileName = "ghost.docx" }), CancellationToken.None);

        Assert.Equal("TARGET_REPORTED_FAILURE", result.ErrorCode);
        Assert.Null(graph.CreateLinkUri);
    }

    [Theory]
    [InlineData("anonymous", "Board pack - CONFIDENTIAL.pdf", null)]
    [InlineData("users", "notes.docx", "Confidential")]
    [InlineData("anonymous", "Client-Memo v2.docx", null)]
    public async Task Graph_ConfidentialDocument_NeverLeavesTheOrganization(string scope, string fileName, string? classification)
    {
        var graph = new FakeGraph();
        var adapter = Adapter(graph, Configured());

        var result = await adapter.ExecuteAsync(
            Req("Document.Share", "SHAREPOINT", new { itemId = "I1", fileName, scope, classification }), CancellationToken.None);

        Assert.Equal("BLOCKED_CONFIDENTIAL_EXPOSURE", result.ErrorCode);
        Assert.Null(graph.CreateLinkUri);   // blocked before Graph is asked to create anything
    }

    [Fact]
    public async Task Graph_ConfidentialDocument_CanStillBeSharedInsideTheOrganization()
    {
        var graph = new FakeGraph();
        var adapter = Adapter(graph, Configured());

        var result = await adapter.ExecuteAsync(
            Req("Document.Share", "SHAREPOINT", new { itemId = "I1", fileName = "CONFIDENTIAL budget.xlsx", scope = "organization" }), CancellationToken.None);

        Assert.Equal(AdapterExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Graph_RefusedByGraph_SurfacesTheStatus()
    {
        var graph = new FakeGraph { CreateLinkStatus = HttpStatusCode.Forbidden };
        var adapter = Adapter(graph, Configured());

        var result = await adapter.ExecuteAsync(Req("Document.Share", "SHAREPOINT", new { itemId = "I1" }), CancellationToken.None);

        Assert.Equal("HTTP_403", result.ErrorCode);
    }

    [Fact]
    public async Task Graph_TokenFailure_IsUnavailableNotACrash()
    {
        var graph = new FakeGraph { TokenStatus = HttpStatusCode.Unauthorized };
        var adapter = Adapter(graph, Configured());

        var result = await adapter.ExecuteAsync(Req("Document.Share", "SHAREPOINT", new { itemId = "I1" }), CancellationToken.None);

        Assert.Equal("TARGET_UNAVAILABLE", result.ErrorCode);
    }

    // ================================================================ helpers

    private static GraphShareAdapter Adapter(FakeGraph graph, GraphOptions options)
        => new(new HttpClient(graph), options, NullLogger<GraphShareAdapter>.Instance);

    private static GraphOptions Configured() => new()
    {
        TenantId = "tenant", ClientId = "client", ClientSecret = "secret", DefaultDriveId = "DEFAULT-DRIVE"
    };

    private static AdapterRequest Req(string actionId, string targetSystem, object parameters)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        return new AdapterRequest
        {
            ExecutionId = Guid.NewGuid(), ActionRequestId = Guid.NewGuid(),
            ActionId = actionId, TargetSystem = targetSystem, Parameters = doc.RootElement.Clone()
        };
    }

    /// <summary>A stand-in for login.microsoftonline.com and graph.microsoft.com that records what it was asked.</summary>
    private sealed class FakeGraph : HttpMessageHandler
    {
        public HttpStatusCode TokenStatus = HttpStatusCode.OK;
        public HttpStatusCode CreateLinkStatus = HttpStatusCode.OK;
        public string? PathLookupId;
        public string? SearchHitId;

        public int Calls;
        public List<string> LookupUris { get; } = new();
        public string? CreateLinkUri;
        public JsonElement? CreateLinkBody;
        public string? CreateLinkAuthorization;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var uri = request.RequestUri!.OriginalString;

            if (uri.StartsWith("https://login.microsoftonline.com/", StringComparison.Ordinal))
                return Json(TokenStatus, "{\"access_token\":\"tok-1\",\"expires_in\":3600}");

            if (uri.EndsWith("/createLink", StringComparison.Ordinal))
            {
                CreateLinkUri = uri;
                CreateLinkAuthorization = request.Headers.Authorization?.ToString();
                CreateLinkBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                return Json(CreateLinkStatus, "{\"id\":\"LINK-1\",\"link\":{\"webUrl\":\"https://contoso.sharepoint.com/x\"}}");
            }

            LookupUris.Add(uri);
            if (uri.Contains("/root:/", StringComparison.Ordinal))
                return PathLookupId is null ? Json(HttpStatusCode.NotFound, "{}") : Json(HttpStatusCode.OK, $"{{\"id\":\"{PathLookupId}\"}}");
            if (uri.Contains("/root/search(", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, SearchHitId is null ? "{\"value\":[]}" : $"{{\"value\":[{{\"id\":\"{SearchHitId}\"}}]}}");

            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body)
            => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
