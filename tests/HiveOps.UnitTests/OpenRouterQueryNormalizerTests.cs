using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.AI;

namespace HiveOps.UnitTests;

public sealed class OpenRouterQueryNormalizerTests
{
    [Fact]
    public async Task NormalizeAsync_Should_Parse_Valid_Json_Response()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "choices":[
                {"message":{"content":"{\"search_term\":\"zapatillas nike\",\"entities\":{\"brand\":\"nike\"},\"is_search_query\":true,\"intent\":\"search\",\"confidence\":0.92,\"language\":\"es\",\"brand\":\"Nike\",\"category\":\"calzado\"}"}}
              ]
            }
            """, Encoding.UTF8, "application/json")
        });
        var sut = CreateSut(handler);

        var result = await sut.NormalizeAsync("quiero zapatillas nike", "tenant_industry: deporte");

        result.Should().NotBe(QueryNormalizationResult.Empty);
        result.IsSearchQuery.Should().BeTrue();
        result.SearchTerm.Should().Be("zapatillas nike");
        result.Brand.Should().Be("Nike");
        result.Category.Should().Be("calzado");
        result.Entities.Should().ContainKey("brand");
    }

    [Fact]
    public async Task NormalizeAsync_Should_Return_Empty_When_Disabled()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Should not call http"));
        var sut = CreateSut(handler, new QueryNormalizerOptions { Enabled = false });

        var result = await sut.NormalizeAsync("hola", "tenant_industry: general");

        result.Should().Be(QueryNormalizationResult.Empty);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task NormalizeAsync_Should_Use_Cache_For_Repeated_Requests()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "choices":[
                {"message":{"content":"{\"search_term\":\"ibuprofeno 600\",\"entities\":{\"dosage\":\"600\"},\"is_search_query\":true,\"intent\":\"search\",\"confidence\":0.9,\"language\":\"es\",\"brand\":null,\"category\":\"farmacia\"}"}}
              ]
            }
            """, Encoding.UTF8, "application/json")
        });
        var sut = CreateSut(handler);

        var first = await sut.NormalizeAsync("tenes ibuprfeno 600?", "tenant_industry: farmacia");
        var second = await sut.NormalizeAsync("tenes ibuprfeno 600?", "tenant_industry: farmacia");

        first.SearchTerm.Should().Be("ibuprofeno 600");
        second.SearchTerm.Should().Be("ibuprofeno 600");
        handler.CallCount.Should().Be(1);
    }

    private static OpenRouterQueryNormalizer CreateSut(StubHttpMessageHandler handler, QueryNormalizerOptions? options = null)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var httpClient = new HttpClient(handler);
        var normalizerOptions = Options.Create(options ?? new QueryNormalizerOptions());
        var semanticOptions = Options.Create(new SemanticKernelOptions
        {
            ActiveProfile = "Testing",
            OpenRouter = new OpenRouterOptions
            {
                ApiKey = "test-key",
                Endpoint = "https://openrouter.ai/api/v1",
                AppName = "HiveOps.Tests",
                Referer = "https://localhost/tests"
            },
            Profiles = new Dictionary<string, ChatProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Testing"] = new() { Enabled = true, ModelId = "meta-llama/llama-3.1-8b-instruct:free", Description = "test" },
                ["Normalizer"] = new() { Enabled = true, ModelId = "openai/gpt-4o-mini", Description = "normalizer" }
            }
        });

        return new OpenRouterQueryNormalizer(
            httpClient,
            memoryCache,
            normalizerOptions,
            semanticOptions,
            NullLogger<OpenRouterQueryNormalizer>.Instance);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
        public int CallCount { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _factory = factory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_factory(request));
        }
    }
}
