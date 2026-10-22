using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using HiveOps.Infrastructure.AI;

namespace HiveOps.UnitTests.AI;

/// <summary>
/// FailoverChatCompletionService: ordered provider chain must fall through on
/// provider errors (auth 401, rate-limit 429, unavailable model) and never
/// swallow cancellation.
/// </summary>
public sealed class FailoverChatCompletionServiceTests
{
    public FailoverChatCompletionServiceTests()
    {
        // The provider circuit is process-wide; isolate each test case.
        FailoverChatCompletionService.ResetCircuit();
    }

    private static FailoverChatCompletionService Build(params (string Name, IChatCompletionService Svc)[] providers)
        => new(providers.Select(p => (p.Name, (IChatCompletionService)p.Svc)),
            NullLogger<FailoverChatCompletionService>.Instance);

    [Fact]
    public async Task Returns_First_Provider_When_It_Succeeds()
    {
        var ok = new StubChat("p1-answer");
        var failover = Build(("one", ok), ("two", new StubChat("p2-answer")));

        var result = await failover.GetChatMessageContentsAsync(new ChatHistory("hi"));

        Assert.Single(result);
        Assert.Equal("p1-answer", result[0].Content);
        Assert.Equal(1, ok.Calls);
    }

    [Fact]
    public async Task Falls_Back_To_Second_Provider_On_Failure()
    {
        var fail = new StubChat(null, new InvalidOperationException("401 Unauthorized"));
        var ok = new StubChat("backup-answer");
        var failover = Build(("openrouter", fail), ("groq", ok));

        var result = await failover.GetChatMessageContentsAsync(new ChatHistory("hi"));

        Assert.Equal("backup-answer", result[0].Content);
        Assert.Equal(1, fail.Calls);
        Assert.Equal(1, ok.Calls);
    }

    [Fact]
    public async Task Walks_The_Whole_Chain_And_Rethrows_Last_Error()
    {
        var failover = Build(
            ("a", new StubChat(null, new Exception("429"))),
            ("b", new StubChat(null, new Exception("503"))),
            ("c", new StubChat(null, new InvalidOperationException("all down"))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failover.GetChatMessageContentsAsync(new ChatHistory("hi")));
        Assert.Equal("all down", ex.Message);
    }

    [Fact]
    public async Task Cancellation_Is_Not_Treated_As_Provider_Failure()
    {
        var cancel = new StubChat(null, new OperationCanceledException());
        var ok = new StubChat("should-not-run");
        var failover = Build(("a", cancel), ("b", ok));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => failover.GetChatMessageContentsAsync(new ChatHistory("hi")));
        Assert.Equal(0, ok.Calls);
    }

    private sealed class StubChat : IChatCompletionService
    {
        private readonly string? _answer;
        private readonly Exception? _error;

        public StubChat(string? answer, Exception? error = null)
        {
            _answer = answer;
            _error = error;
        }

        public int Calls { get; private set; }
        public IReadOnlyDictionary<string, object?> Attributes { get; } =
            new Dictionary<string, object?> { ["test"] = true };

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_error is not null) throw _error;
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>(
                [new ChatMessageContent(AuthorRole.Assistant, _answer)]);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_error is not null) throw _error;
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, _answer);
            await Task.CompletedTask;
        }
    }
}
