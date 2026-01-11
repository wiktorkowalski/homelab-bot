import { useParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { conversationsApi } from '../api/client'

export default function ConversationDetail() {
  const { threadId } = useParams<{ threadId: string }>()

  const { data, isLoading, error } = useQuery({
    queryKey: ['conversation', threadId],
    queryFn: () => conversationsApi.getByThreadId(threadId!),
    enabled: !!threadId,
  })

  if (isLoading) return <div className="text-gray-400">Loading...</div>
  if (error) return <div className="text-red-400">Error loading conversation</div>
  if (!data) return <div className="text-gray-400">Conversation not found</div>

  return (
    <div>
      <Link to="/conversations" className="text-blue-400 hover:underline mb-4 block">
        ← Back to Conversations
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
        {data.messages.map((msg) => (
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
              <span className="text-xs text-gray-500">
                {new Date(msg.timestamp).toLocaleString()}
              </span>
            </div>
            <div className="whitespace-pre-wrap">{msg.content}</div>
          </div>
        ))}
      </div>
    </div>
  )
}
