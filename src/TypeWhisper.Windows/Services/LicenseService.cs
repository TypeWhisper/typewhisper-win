using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using TypeWhisper.Windows.Exceptions;

namespace TypeWhisper.Windows.Services
{
    public static class PolarApiVersion
    {
        public const string Version = "2026-04";
    }

    public class LicenseService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public LicenseService(HttpClient httpClient, string baseUrl)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        }

        public async Task ActivateCoreAsync(string activationId, string credentials)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/activate")
            {
                Content = JsonContent.Create(new { activationId, credentials })
            };

            request.Headers.Add("Polar-Version", PolarApiVersion.Version);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        public async Task ValidateCoreAsync(string activationId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/validate")
            {
                Content = JsonContent.Create(new { activationId })
            };

            request.Headers.Add("Polar-Version", PolarApiVersion.Version);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        public async Task DeactivateCoreAsync(string activationId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/deactivate")
            {
                Content = JsonContent.Create(new { activationId })
            };

            request.Headers.Add("Polar-Version", PolarApiVersion.Version);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        public bool IsPolarResourceMissing(PolarApiException exception)
        {
            if (exception.StatusCode != HttpStatusCode.NotFound)
                return false;

            // Only treat as missing if response contains documented 'not found' error structure
            return exception.ResponseContent.Contains("error": "not_found", ignoreCase: true);
        }
    }
}