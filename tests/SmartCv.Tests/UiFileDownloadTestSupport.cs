extern alias web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using SmartCv.Core.Options;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

internal static class UiFileDownloadTestSupport
{
    // Old render fixtures predate B43's authenticated file-transfer component.
    // Register the real service with each fixture's own API handler and synthetic session.
    internal static IServiceCollection AddFixtureFileDownloads(this IServiceCollection services, HttpClient http)
    {
        services.TryAddSingleton<IJSRuntime, NoJs>();
        services.TryAddSingleton(_ => B43UiTestSupport.Session());
        services.TryAddSingleton<AnonymityState>();
        services.TryAddSingleton<FileDownloadService>();
        services.TryAddSingleton(new CvApiClient(http, Options.Create(new CvUploadOptions()), NullLogger<CvApiClient>.Instance));
        return services;
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) =>
            ValueTask.FromResult(identifier == "import" ? (T)(object)new Module() : default(T)!);
    }
    private sealed class Module : IJSObjectReference
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(typeof(T) == typeof(bool) ? (T)(object)true : default(T)!);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
