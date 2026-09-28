using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using TypeWhisper.Windows.Exceptions;
using TypeWhisper.Windows.Services;
using Xunit;

namespace TypeWhisper.PluginSystem.Tests
{
    public class LicenseServiceTests
    {
        [Fact]
        public async Task ActivateCoreAsync_ShouldIncludePolarVersionHeader()
        {
            // Arrange
            var handlerMock = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(handlerMock.Object);
            var licenseService = new LicenseService(httpClient, "https://polar.example.com");

            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK
                });

            // Act
            await licenseService.ActivateCoreAsync("test-activation", "test-credentials");

            // Assert
            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Exactly(1),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Headers.Contains("Polar-Version") &&
                    req.Headers.GetValues("Polar-Version").First() == PolarApiVersion.Version),
                ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task IsPolarResourceMissing_ShouldReturnFalseForVersionErrors()
        {
            // Arrange
            var licenseService = new LicenseService(new HttpClient(), "https://polar.example.com");
            var exception = new PolarApiException
            {
                StatusCode = HttpStatusCode.NotFound,
                ResponseContent = "{ \"error\": \"invalid_version\", \"message\": \"Version 2026-10 not found\" }"
            };

            // Act
            var result = licenseService.IsPolarResourceMissing(exception);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task IsPolarResourceMissing_ShouldReturnTrueForGenuineMissingActivations()
        {
            // Arrange
            var licenseService = new LicenseService(new HttpClient(), "https://polar.example.com");
            var exception = new PolarApiException
            {
                StatusCode = HttpStatusCode.NotFound,
                ResponseContent = "{ \"error\": \"not_found\", \"message\": \"Activation not found\" }"
            };

            // Act
            var result = licenseService.IsPolarResourceMissing(exception);

            // Assert
            Assert.True(result);
        }
    }
}