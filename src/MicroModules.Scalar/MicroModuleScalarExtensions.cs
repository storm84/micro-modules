using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Scalar.AspNetCore;

namespace MicroModules.Scalar;

public static class MicroModuleScalarExtensions
{
    /// <summary>Registers the default v1 OpenAPI document used by the Scalar reference.</summary>
    public static IServiceCollection AddMicroModulesScalar(
        this IServiceCollection services,
        Action<OpenApiOptions>? configureOpenApi = null)
        => services.AddOpenApi("v1", options => configureOpenApi?.Invoke(options));

    /// <summary>Maps /openapi/{documentName}.json and the Scalar reference at /scalar by default.</summary>
    public static RouteGroupBuilder MapMicroModulesScalar(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/scalar",
        Action<ScalarOptions>? configureScalar = null)
    {
        var group = endpoints.MapGroup("").ExcludeFromDescription();
        group.MapOpenApi();
        group.MapScalarApiReference(pattern, options => configureScalar?.Invoke(options));
        return group;
    }
}
