using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using MicroModules.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;

namespace MicroModules.Scalar.Tests;

public sealed class ScalarIntegrationTests
{
    [Fact]
    public async Task OpenApi_describes_contract_bodies_responses_and_module_tags()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        var operation = paths.GetProperty("/modules/catalog/v2/search").GetProperty("post");
        Assert.Equal("catalog.v2.search", operation.GetProperty("operationId").GetString());
        Assert.Contains(operation.GetProperty("tags").EnumerateArray(), tag => tag.GetString() == "catalog");
        var requestBody = operation.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());
        var request = Resolve(root, requestBody.GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        Assert.True(HasType(request.GetProperty("properties").GetProperty("query"), "string"));
        Assert.True(HasType(request.GetProperty("properties").GetProperty("limit"), "integer"));
        var response = Resolve(root, operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var items = response.GetProperty("properties").GetProperty("items");
        Assert.True(HasType(items, "array"));
        var item = Resolve(root, items.GetProperty("items"));
        Assert.True(HasType(item.GetProperty("properties").GetProperty("name"), "string"));
        Assert.True(operation.GetProperty("responses").GetProperty("400").GetProperty("content")
            .TryGetProperty("application/problem+json", out _));
        Assert.True(paths.TryGetProperty("/status", out _));
        Assert.DoesNotContain(paths.EnumerateObject(), path => path.Name.StartsWith("/scalar")
            || path.Name.StartsWith("/openapi") || path.Name.Contains("/remote/"));
    }

    [Fact]
    public async Task Task_operations_document_no_content_and_keep_working()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var responses = document.RootElement.GetProperty("paths").GetProperty("/modules/catalog/v2/notify")
            .GetProperty("post").GetProperty("responses");
        Assert.True(responses.TryGetProperty("204", out var noContent));
        Assert.False(noContent.TryGetProperty("content", out _));
        Assert.False(responses.TryGetProperty("200", out _));
        using var response = await client.PostAsJsonAsync("/modules/catalog/v2/notify", new SearchRequest("demo", 1));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Scalar_serves_html_and_allows_custom_route_and_configuration()
    {
        await using var app = await StartAsync(customize: true);
        using var client = app.GetTestClient();
        using var redirect = await client.GetAsync("/docs");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("/docs/", new Uri(new Uri(client.BaseAddress!, "/docs"), redirect.Headers.Location!).AbsolutePath);
        using var response = await client.GetAsync(redirect.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Custom reference", html);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        Assert.Equal("Custom document", document.RootElement.GetProperty("info").GetProperty("title").GetString());
        using var defaultRoute = await client.GetAsync("/scalar");
        Assert.Equal(HttpStatusCode.NotFound, defaultRoute.StatusCode);
    }

    [Fact]
    public async Task Default_scalar_route_serves_the_reference()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var redirect = await client.GetAsync("/scalar");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("/scalar/", new Uri(new Uri(client.BaseAddress!, "/scalar"), redirect.Headers.Location!).AbsolutePath);
        using var response = await client.GetAsync(redirect.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("scalar", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hosts_without_opt_in_have_no_documentation_routes_and_modules_still_work()
    {
        await using var app = await StartAsync(enableScalar: false);
        using var client = app.GetTestClient();
        foreach (var path in new[] { "/scalar", "/openapi/v1.json" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var result = await client.PostAsJsonAsync("/modules/catalog/v2/search", new SearchRequest("demo", 1));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var body = await result.Content.ReadFromJsonAsync<SearchResult>();
        Assert.Equal("demo", Assert.Single(body!.Items).Name);
    }

    [Fact]
    public async Task Route_group_authorization_protects_both_document_and_ui()
    {
        await using var app = await StartAsync(protectDocumentation: true);
        using var client = app.GetTestClient();
        foreach (var path in new[] { "/scalar", "/scalar/", "/openapi/v1.json" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var status = await client.GetAsync("/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    private static bool HasType(JsonElement schema, string expected)
    {
        var type = schema.GetProperty("type");
        return type.ValueKind == JsonValueKind.String
            ? type.GetString() == expected
            : type.EnumerateArray().Any(value => value.GetString() == expected);
    }

    private static JsonElement Resolve(JsonElement document, JsonElement schema)
        => schema.TryGetProperty("$ref", out var reference)
            ? document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1])
            : schema;

    private static async Task<WebApplication> StartAsync(bool enableScalar = true, bool customize = false, bool protectDocumentation = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        if (protectDocumentation)
        {
            builder.Services.AddAuthentication("test")
                .AddScheme<AuthenticationSchemeOptions, AnonymousHandler>("test", _ => { });
            builder.Services.AddAuthorization();
        }
        builder.Services.AddMicroModules(modules =>
        {
            modules.AddLocal<ICatalogModule, CatalogModule>();
            modules.AddRemote<IRemoteModule>(options => options.Endpoints.Add(new Uri("http://elsewhere")));
        });
        if (enableScalar)
            builder.Services.AddMicroModulesScalar(options =>
            {
                if (customize)
                    options.AddDocumentTransformer((document, _, _) =>
                    {
                        document.Info.Title = "Custom document";
                        return Task.CompletedTask;
                    });
            });
        var app = builder.Build();
        app.MapMicroModules();
        app.MapGet("/status", () => new { Ready = true });
        if (enableScalar)
        {
            var documentation = app.MapMicroModulesScalar(customize ? "/docs" : "/scalar",
                options => { if (customize) options.WithTitle("Custom reference"); });
            if (protectDocumentation) documentation.RequireAuthorization();
        }
        await app.StartAsync();
        return app;
    }
}

public sealed record SearchRequest(string Query, int Limit);
public sealed record Item(Guid Id, string Name);
public sealed record SearchResult(IReadOnlyList<Item> Items);

[ModuleContract("catalog", Version = 2)]
public interface ICatalogModule
{
    [ModuleOperation("search", RetrySafe = true)]
    Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken token);
    [ModuleOperation("notify")]
    Task NotifyAsync(SearchRequest request, CancellationToken token);
}

public sealed class CatalogModule : ICatalogModule
{
    public Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken token)
        => Task.FromResult(new SearchResult([new Item(Guid.Empty, request.Query)]));
    public Task NotifyAsync(SearchRequest request, CancellationToken token) => Task.CompletedTask;
}

[ModuleContract("remote")]
public interface IRemoteModule
{
    [ModuleOperation("search", RetrySafe = true)]
    Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken token);
}

public sealed class AnonymousHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.NoResult());
}
