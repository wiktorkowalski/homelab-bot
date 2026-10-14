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
        // Same shape the function-invocation loop stores: FunctionResultContent only, no text.
        history.Add(new ChatMessageContent(AuthorRole.Tool, [new FunctionResultContent("Recall", "Knowledge", callId, result)]));
    }

    [Fact]
    public void TruncateToolResults_CapsLargeResultAndKeepsCallId()
    {
        var history = NewHistory();
        AddToolTurn(history, "call-1", new string('a', 10_000));

        var count = ConversationService.TruncateToolResults(history, 1_000);

        Assert.Equal(1, count);
        var tool = history[2];
        Assert.True(ConversationService.EstimateTokens(tool) < 300);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(tool.Items));
        Assert.Equal("call-1", result.CallId);
        var text = Assert.IsType<string>(result.Result);
        Assert.StartsWith(new string('a', 900), text);
        Assert.EndsWith(" of 10000 chars]", text);

        // The marker is part of the 1000 chars and says how many chars of the result are left.
        Assert.True(text.Length <= 1_000);
        Assert.Contains($"[truncated: kept {text.IndexOf('\n')} of 10000 chars]", text);
    }

    [Fact]
    public void TruncateToolResults_SecondPass_LeavesACappedResultAlone()
    {
        var history = NewHistory();
        AddToolTurn(history, "call-1", new string('a', 50_000));
        ConversationService.TruncateToolResults(history, 8_000);

        var count = ConversationService.TruncateToolResults(history, 8_000);

        Assert.Equal(0, count);
        Assert.True(((string)((FunctionResultContent)history[2].Items[0]).Result!).Length <= 8_000);
    }

    [Fact]
    public void TruncateToolResults_LeavesSmallResultAlone()
    {
        var history = NewHistory();
        AddToolTurn(history, "call-1", "small");

        var count = ConversationService.TruncateToolResults(history, 1_000);

        Assert.Equal(0, count);
        Assert.Equal("small", ((FunctionResultContent)history[2].Items[0]).Result);
    }

    [Fact]
    public void HistoryTarget_IsBelowTheLimit()
    {
        // A target at or over the limit would bring back a trim on every message.
        Assert.True(ConversationService.HistoryTarget.Messages < ConversationService.HistoryLimit.Messages);
        Assert.True(ConversationService.HistoryTarget.Tokens < ConversationService.HistoryLimit.Tokens);
    }

    [Fact]
    public void TrimHistory_AtTheLimit_RemovesNothing()
    {
        var history = NewHistory();
        for (var i = 0; i < 20; i++)
        {
            // 20 messages of 100 tokens: exactly at both limits, far over the target.
            history.AddUserMessage(new string('x', 400));
        }

        var removed = ConversationService.TrimHistory(history, limit: (20, 2_000), target: (12, 1_200));

        Assert.Equal(0, removed);
        Assert.Equal(21, history.Count);
    }

    [Fact]
    public void TrimHistory_PastTheMessageLimit_CutsToTheTarget()
    {
        var history = NewHistory();
        for (var i = 0; i < 30; i++)
        {
            history.AddUserMessage($"m{i}");
        }

        ConversationService.TrimHistory(history, limit: (20, 100_000), target: (12, 100_000));

        Assert.Equal(13, history.Count);
        Assert.Equal(AuthorRole.System, history[0].Role);
        Assert.Equal("m18", history[1].Content);
        Assert.Equal("m29", history[^1].Content);
    }

    [Fact]
    public void TrimHistory_PastTheTokenLimit_CutsToTheTarget()
    {
        var history = NewHistory();
        for (var i = 0; i < 10; i++)
        {
            // ~500 tokens each at chars/4.
            history.AddUserMessage(new string('x', 2_000));
        }

        ConversationService.TrimHistory(history, limit: (20, 4_000), target: (20, 2_400));

        // System prompt plus the four newest messages: 2000 tokens.
        Assert.Equal(5, history.Count);
        Assert.Equal(AuthorRole.System, history[0].Role);
    }

    [Fact]
    public void TrimHistory_TokenCut_AlsoReachesTheMessageTarget()
    {
        var history = NewHistory();
        for (var i = 0; i < 10; i++)
        {
            history.AddUserMessage($"q{i}");
            history.AddAssistantMessage(i == 0 ? new string('a', 20_000) : $"a{i}");
        }

        // 20 messages are within the limit; only the 5000 tokens of the first answer pass it.
        ConversationService.TrimHistory(history, limit: (20, 4_000), target: (12, 2_400));

        Assert.Equal(13, history.Count);
        Assert.Equal("q4", history[1].Content);
    }

    [Fact]
    public void TrimHistory_CountCut_AlsoReachesTheTokenTarget()
    {
        var history = NewHistory();
        for (var i = 0; i < 9; i++)
        {
            history.AddUserMessage("m");
        }

        for (var i = 0; i < 12; i++)
        {
            // ~100 tokens each: 1200 in total, within the limit.
            history.AddUserMessage(new string('x', 400));
        }

        // Only the 21 messages pass the limit.
        ConversationService.TrimHistory(history, limit: (20, 1_200), target: (12, 720));

        // Seven messages of 100 tokens fit the 720-token target.
        Assert.Equal(8, history.Count);
    }

    [Fact]
    public void TrimHistory_KeepsNewestMessageEvenOverBudget()
    {
        var history = NewHistory();
        history.AddUserMessage("old");
        history.AddUserMessage(new string('x', 40_000));

        ConversationService.TrimHistory(history, limit: (20, 100), target: (20, 60));

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
        ConversationService.TrimHistory(history, limit: (20, 500), target: (20, 300));

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
        ConversationService.TrimHistory(history, limit: (20, 500), target: (20, 300));

        Assert.Equal(AuthorRole.System, history[0].Role);
        Assert.Equal(AuthorRole.User, history[1].Role);
        Assert.Equal("q2", history[1].Content);
        Assert.Equal(5, history.Count);
        Assert.Equal("a2", history[^1].Content);
    }

    [Fact]
    public void TrimHistory_CutsAtTurnBoundary()
    {
        var history = NewHistory();
        for (var i = 0; i < 4; i++)
        {
            history.AddUserMessage($"q{i}");
            history.AddAssistantMessage($"a{i}");
        }

        // 8 messages, target 5: dropping only 3 would start history with an assistant reply.
        ConversationService.TrimHistory(history, limit: (7, 100_000), target: (5, 100_000));

        Assert.Equal(AuthorRole.User, history[1].Role);
        Assert.Equal("q2", history[1].Content);
        Assert.Equal(5, history.Count);
    }

    [Fact]
    public void EstimateTokens_CountsToolResult()
    {
        var message = new ChatMessageContent(
            AuthorRole.Tool, [new FunctionResultContent("Recall", "Knowledge", "c", new string('z', 4_000))]);

        Assert.Equal(1_000, ConversationService.EstimateTokens(message));
    }

    [Fact]
    public void EstimateTokens_CountsTextAndToolCallOfOneMessage()
    {
        var message = new ChatMessageContent(
            AuthorRole.Assistant,
            [new TextContent(new string('t', 400)), new FunctionCallContent("Recall", "Knowledge", "c")]);

        // 400 chars of text plus 6 of the function name.
        Assert.Equal(101, ConversationService.EstimateTokens(message));
    }
}
