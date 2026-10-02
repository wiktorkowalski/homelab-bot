using HomelabBot.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace HomelabBot.Tests;

public sealed class ConversationHistoryBudgetTests
{
    private static ChatHistory NewHistory()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("system prompt");
        return history;
    }

    private static void AddToolTurn(ChatHistory history, string callId, string result)
    {
        history.Add(new ChatMessageContent(AuthorRole.Assistant, [new FunctionCallContent("Recall", "Knowledge", callId)]));
        // Same shape SK's FunctionCallsProcessor builds: text content plus FunctionResultContent.
        var toolMessage = new ChatMessageContent(AuthorRole.Tool, result);
        toolMessage.Items.Add(new FunctionResultContent("Recall", "Knowledge", callId, result));
        history.Add(toolMessage);
    }

    [Fact]
    public void TruncateToolResults_CapsLargeResultAndKeepsCallId()
    {
        var history = NewHistory();
        AddToolTurn(history, "call-1", new string('a', 10_000));

        var count = ConversationService.TruncateToolResults(history, 1_000);

        Assert.Equal(1, count);
        var tool = history[2];
        Assert.StartsWith(new string('a', 1_000), tool.Content);
        Assert.Contains("[truncated: kept 1000 of 10000 chars]", tool.Content);
        Assert.True(ConversationService.EstimateTokens(tool) < 300);
        var result = Assert.IsType<FunctionResultContent>(tool.Items[1]);
        Assert.Equal("call-1", result.CallId);
        var text = Assert.IsType<string>(result.Result);
        Assert.StartsWith(new string('a', 1_000), text);
        Assert.Contains("[truncated: kept 1000 of 10000 chars]", text);
    }

    [Fact]
    public void TruncateToolResults_LeavesSmallResultAlone()
    {
        var history = NewHistory();
        AddToolTurn(history, "call-1", "small");

        var count = ConversationService.TruncateToolResults(history, 1_000);

        Assert.Equal(0, count);
        Assert.Equal("small", history[2].Content);
        Assert.Equal("small", ((FunctionResultContent)history[2].Items[1]).Result);
    }

    [Fact]
    public void TrimHistory_EnforcesMessageCount()
    {
        var history = NewHistory();
        for (var i = 0; i < 30; i++)
        {
            history.AddUserMessage($"m{i}");
        }

        ConversationService.TrimHistory(history, maxMessages: 20, maxTokens: 100_000);

        Assert.Equal(21, history.Count);
        Assert.Equal(AuthorRole.System, history[0].Role);
        Assert.Equal("m10", history[1].Content);
        Assert.Equal("m29", history[^1].Content);
    }

    [Fact]
    public void TrimHistory_EnforcesTokenBudget()
    {
        var history = NewHistory();
        for (var i = 0; i < 5; i++)
        {
            // ~1000 tokens each at chars/4.
            history.AddUserMessage(new string('x', 4_000));
        }

        ConversationService.TrimHistory(history, maxMessages: 20, maxTokens: 2_500);

        // System prompt plus the two newest messages.
        Assert.Equal(3, history.Count);
        Assert.Equal(AuthorRole.System, history[0].Role);
    }

    [Fact]
    public void TrimHistory_KeepsNewestMessageEvenOverBudget()
    {
        var history = NewHistory();
        history.AddUserMessage("old");
        history.AddUserMessage(new string('x', 40_000));

        ConversationService.TrimHistory(history, maxMessages: 20, maxTokens: 100);

        Assert.Equal(2, history.Count);
        Assert.Equal(40_000, history[1].Content!.Length);
    }

    [Fact]
    public void TrimHistory_DropsOrphanedToolResults()
    {
        var history = NewHistory();
        AddToolTurn(history, "call-1", new string('r', 8_000));
        history.AddAssistantMessage("answer");
        history.AddUserMessage("next question");

        // The budget forces out the assistant tool call; its tool result must go with it.
        ConversationService.TrimHistory(history, maxMessages: 20, maxTokens: 500);

        Assert.DoesNotContain(history, m => m.Role == AuthorRole.Tool);
        Assert.Equal(2, history.Count);
        Assert.Equal("next question", history[^1].Content);
    }

    [Fact]
    public void TrimHistory_DropsWholeTurnsAndKeepsCurrentTurn()
    {
        var history = NewHistory();
        history.AddUserMessage("q1");
        history.AddAssistantMessage(new string('a', 4_000));
        history.AddUserMessage("q2");
        AddToolTurn(history, "call-2", new string('r', 40_000));
        history.AddAssistantMessage("a2");

        // The current turn alone is ~10k tokens; it stays whole even though it exceeds the budget.
        ConversationService.TrimHistory(history, maxMessages: 20, maxTokens: 500);

        Assert.Equal(AuthorRole.System, history[0].Role);
        Assert.Equal(AuthorRole.User, history[1].Role);
        Assert.Equal("q2", history[1].Content);
        Assert.Equal(5, history.Count);
        Assert.Equal("a2", history[^1].Content);
    }

    [Fact]
    public void TrimHistory_CountCapCutsAtTurnBoundary()
    {
        var history = NewHistory();
        for (var i = 0; i < 4; i++)
        {
            history.AddUserMessage($"q{i}");
            history.AddAssistantMessage($"a{i}");
        }

        // 8 messages, cap 5: dropping only 3 would start history with an assistant reply.
        ConversationService.TrimHistory(history, maxMessages: 5, maxTokens: 100_000);

        Assert.Equal(AuthorRole.User, history[1].Role);
        Assert.Equal("q2", history[1].Content);
        Assert.Equal(5, history.Count);
    }

    [Fact]
    public void EstimateTokens_CountsToolResultOnce()
    {
        var text = new string('z', 4_000);
        var message = new ChatMessageContent(AuthorRole.Tool, text);
        message.Items.Add(new FunctionResultContent("Recall", "Knowledge", "c", text));

        Assert.Equal(1_000, ConversationService.EstimateTokens(message));
    }

    [Fact]
    public void EstimateTokens_CountsBareToolResult()
    {
        var message = new ChatMessageContent(
            AuthorRole.Tool, [new FunctionResultContent("Recall", "Knowledge", "c", new string('z', 4_000))]);

        Assert.Equal(1_000, ConversationService.EstimateTokens(message));
    }
}
