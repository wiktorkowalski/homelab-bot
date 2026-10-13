using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace HomelabBot.Services;

// Shapes every request for the Claude API: effort, prompt-cache breakpoints and a stable tool
// order. It sits below the function-invocation loop, so each tool round passes through it.
// The caller's messages and options are never mutated: the loop above reuses them for the next
// round, and a breakpoint left on an old message would add up past the API limit of four.
internal sealed class AnthropicRequestChatClient : DelegatingChatClient
{
    private static readonly JsonElement EmptyObjectSchema =
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    private readonly ReasoningEffort _effort;

    public AnthropicRequestChatClient(IChatClient innerClient, ReasoningEffort effort)
        : base(innerClient)
    {
        _effort = effort;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (shapedMessages, shapedOptions) = Shape(messages, options, _effort);
        return base.GetResponseAsync(shapedMessages, shapedOptions, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (shapedMessages, shapedOptions) = Shape(messages, options, _effort);
        return base.GetStreamingResponseAsync(shapedMessages, shapedOptions, cancellationToken);
    }

    // Breakpoints (the render order is tools, system, messages; a breakpoint caches everything
    // before it):
    // - last tool: the tool schemas alone, shared by every system prompt that uses the same tools;
    // - system prompt: tools + system, reused by every round and by other interactions in the TTL;
    // - last message: only when tools are offered, so the next tool round reads this round's
    //   prefix. A single-round request would pay the 1.25x write for nothing.
    internal static (List<ChatMessage> Messages, ChatOptions Options) Shape(
        IEnumerable<ChatMessage> messages, ChatOptions? options, ReasoningEffort effort)
    {
        var shapedOptions = options?.Clone() ?? new ChatOptions();
        shapedOptions.Reasoning ??= new ReasoningOptions { Effort = effort };

        var shapedMessages = messages.ToList();
        var offersTools = shapedOptions.Tools is { Count: > 0 };
        if (offersTools)
        {
            shapedOptions.Tools = OrderAndMarkTools(shapedOptions.Tools!);
        }
        else
        {
            DeclareToolsOfEarlierTurns(shapedMessages, shapedOptions);
        }

        MarkLastContent(shapedMessages, shapedMessages.FindLastIndex(m => m.Role == ChatRole.System));
        if (offersTools && shapedMessages.Count > 0 && shapedMessages[^1].Role != ChatRole.System)
        {
            MarkLastContent(shapedMessages, shapedMessages.Count - 1);
        }

        return (shapedMessages, shapedOptions);
    }

    // The tool list is part of the cached prefix: one changed byte or a different order is a
    // cache miss on everything after it, so the order must not depend on registration order.
    private static List<AITool> OrderAndMarkTools(IList<AITool> tools)
    {
        var ordered = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        if (ordered[^1] is AIFunction last)
        {
            ordered[^1] = new CacheBreakpointFunction(last);
        }

        return ordered;
    }

    // A request without tools can still hold tool turns: an earlier turn of the thread used
    // tools, or the function-invocation loop removed them for its last round. The API rejects
    // tool_use / tool_result blocks when no tool is defined. So the tools those turns name are
    // declared by name only, with tool_choice "none": the model cannot call them, their schemas
    // stay out of the prompt, and a tool result stays a tool_result block. Turning it into
    // plain text would hand tool output (log lines, alert texts) to the model as if the user
    // had typed it.
    private static void DeclareToolsOfEarlierTurns(List<ChatMessage> messages, ChatOptions options)
    {
        var names = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(call => call.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            return;
        }

        options.Tools = [.. names.Select(name => (AITool)AIFunctionFactory.CreateDeclaration(
            name, "Used earlier in this conversation. Not available now.", EmptyObjectSchema))];
        options.ToolMode = ChatToolMode.None;

        // The last round of the loop still carries the thinking blocks of its earlier rounds.
        // A thinking block is only valid with the tool list it was produced with, and that list
        // is now different, so the API would reject the request. Every tool call has its result
        // by now, so the blocks can go.
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (!message.Contents.Any(content => content is TextReasoningContent))
            {
                continue;
            }

            var kept = message.Contents.Where(content => content is not TextReasoningContent).ToList();
            if (kept.Count == 0)
            {
                messages.RemoveAt(i);
            }
            else
            {
                messages[i] = new ChatMessage(message.Role, kept) { AuthorName = message.AuthorName };
            }
        }
    }

    // Replaces the message with a copy whose last block carries the breakpoint. Only text and
    // tool-result blocks are copied; any other block type is left unmarked.
    private static void MarkLastContent(List<ChatMessage> messages, int index)
    {
        if (index < 0 || messages[index].Contents.Count == 0)
        {
            return;
        }

        var message = messages[index];
        AIContent? marked = message.Contents[^1] switch
        {
            TextContent text => new TextContent(text.Text),
            FunctionResultContent result => new FunctionResultContent(result.CallId, result.Result) { Exception = result.Exception },
            _ => null,
        };

        if (marked is null)
        {
            return;
        }

        marked.WithCacheControl(Ephemeral());
        messages[index] = new ChatMessage(message.Role, [.. message.Contents.Take(message.Contents.Count - 1), marked])
        {
            AuthorName = message.AuthorName,
        };
    }

    // Default 5-minute TTL: tool rounds of one interaction start seconds apart, so the 2x write
    // price of the 1-hour TTL buys nothing.
    private static CacheControlEphemeral Ephemeral() => new();

    private sealed class CacheBreakpointFunction : DelegatingAIFunction
    {
        public CacheBreakpointFunction(AIFunction innerFunction)
            : base(innerFunction)
        {
            AdditionalProperties = new Dictionary<string, object?>(innerFunction.AdditionalProperties)
            {
                [nameof(Tool.CacheControl)] = Ephemeral(),
            };
        }

        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; }
    }
}
