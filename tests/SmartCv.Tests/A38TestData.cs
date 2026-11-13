using SmartCv.Core.Uploads;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SmartCv.Tests;
internal static class A38TestData
{
    // Explicit synthetic consent for pre-A38 upload regression fixtures, never production defaults.
    internal static CvUploadSource ConfirmedSource() => CvUploadSource.Create("Email", true, DateTime.UtcNow).Source!;
    internal static MultipartFormDataContent WithConsent(MultipartFormDataContent content)
    {
        content.Add(new StringContent("Email"), "sourceChannel");
        content.Add(new StringContent("true"), "consentConfirmed");
        return content;
    }

    // Only legacy A4/A5/A8 regression fixtures use this explicit synthetic request metadata.
    // Production Web still sends no consent until B38; A38ConsentEndpointTests separately proves its HTTP 400.
    internal static HttpClient ClientWithSyntheticConsent(WebApplicationFactory<Program> api) => api.CreateDefaultClient(new SyntheticConsentHandler());
    private sealed class SyntheticConsentHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is MultipartFormDataContent form)
                WithConsent(form);
            return base.SendAsync(request, ct);
        }
    }
}
