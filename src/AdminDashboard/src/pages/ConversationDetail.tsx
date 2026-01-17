import { useParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { conversationsApi, telemetryApi } from '../api/client'
import type { LlmInteraction } from '../api/client'

export default function ConversationDetail() {
  const { threadId } = useParams<{ threadId: string }>()

  const { data, isLoading, error } = useQuery({
    queryKey: ['conversation', threadId],
    queryFn: () => conversationsApi.getByThreadId(threadId!),
    enabled: !!threadId,
  })

  const { data: telemetryData } = useQuery({
    queryKey: ['telemetry', 'thread', threadId],
    queryFn: () => telemetryApi.getAll({ threadId, pageSize: 100 }),
    enabled: !!threadId,
  })

  // Build a map of assistant message index -> telemetry
  // Both are ordered chronologically, so we can pair them up
  const telemetryMap = new Map<number, LlmInteraction>()
  if (data?.messages && telemetryData?.items) {
    const assistantMsgs = data.messages
      .map((m, i) => ({ index: i, msg: m }))
      .filter((x) => x.msg.role === 'assistant')
    const sortedTelemetry = [...telemetryData.items].sort(
      (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
    )
    assistantMsgs.forEach((x, i) => {
      if (sortedTelemetry[i]) {
        telemetryMap.set(x.index, sortedTelemetry[i])
      }
    })
  }

  const getTelemetry = (msgIndex: number): LlmInteraction | undefined => {
    return telemetryMap.get(msgIndex)
  }

  if (isLoading) return <div className="text-gray-400">Loading...</div>
  if (error) return <div className="text-red-400">Error loading conversation</div>
  if (!data) return <div className="text-gray-400">Conversation not found</div>

  return (
    <div>
      <Link to="/conversations" className="text-blue-400 hover:underline mb-4 block">
        &larr; Back to Conversations
      </Link>

      <div className="flex justify-between items-start mb-6">
        <div>
          <h1 className="text-2xl font-bold">{data.title || 'Untitled Conversation'}</h1>
          <div className="text-gray-400 text-sm mt-1">
            Thread ID: <span className="font-mono">{data.threadId}</span>
          </div>
        </div>
        <div className="text-right text-sm text-gray-400">
          <div>Created: {new Date(data.createdAt).toLocaleString()}</div>
          {data.lastMessageAt && (
            <div>Last activity: {new Date(data.lastMessageAt).toLocaleString()}</div>
          )}
        </div>
      </div>

      <div className="space-y-4">
        {data.messages.map((msg, index) => {
          const telemetry = msg.role === 'assistant' ? getTelemetry(index) : undefined

          return (
            <div
              key={msg.id}
              className={`p-4 rounded-lg ${
                msg.role === 'user'
                  ? 'bg-blue-900/30 ml-8'
                  : msg.role === 'assistant'
                  ? 'bg-gray-800 mr-8'
                  : 'bg-gray-700 text-sm'
              }`}
            >
              <div className="flex justify-between items-center mb-2">
                <span
                  className={`text-sm font-semibold ${
                    msg.role === 'user'
                      ? 'text-blue-400'
                      : msg.role === 'assistant'
                      ? 'text-green-400'
                      : 'text-gray-400'
                  }`}
                >
                  {msg.role}
                </span>
                <div className="flex items-center gap-3">
                  {telemetry && (
                    <Link
                      to={`/telemetry/${telemetry.id}`}
                      className="text-xs text-purple-400 hover:underline"
                    >
                      {telemetry.toolCallCount > 0
                        ? `${telemetry.toolCallCount} tool calls`
                        : 'View telemetry'}
                    </Link>
                  )}
                  <span className="text-xs text-gray-500">
                    {new Date(msg.timestamp).toLocaleString()}
                  </span>
                </div>
              </div>
              <div className="whitespace-pre-wrap">{msg.content}</div>
              {telemetry && (
                <div className="mt-2 pt-2 border-t border-gray-700 text-xs text-gray-500 flex gap-4">
                  <span>{telemetry.promptTokens ?? 0} + {telemetry.completionTokens ?? 0} tokens</span>
                  <span>{telemetry.latencyMs}ms</span>
                </div>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}
