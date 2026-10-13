using Microsoft.Extensions.AI;

namespace HomelabBot.Services;

// A refusal (stop_reason "refusal", reported as ContentFilter) can arrive with tool calls the
// model wrote before the classifier stopped it. The function-invocation loop above does not look
// at the stop reason: it would run those tools, and the next round would replace the refusal, so
// the caller never sees it. Removing the calls here ends the loop on the refused response.
internal sealed class AnthropicRefusalChatClient : DelegatingChatClient
{
    public AnthropicRefusalChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        if (response.FinishReason == ChatFinishReason.ContentFilter)
        {
            foreach (var message in response.Messages)
            {
                message.Contents = message.Contents.Where(content => content is not FunctionCallContent).ToList();
            }
        }

        return response;
    }
}
