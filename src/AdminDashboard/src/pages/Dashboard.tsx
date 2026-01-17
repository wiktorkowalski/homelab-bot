import { useQuery } from '@tanstack/react-query'
import { telemetryApi, knowledgeApi, conversationsApi } from '../api/client'

export default function Dashboard() {
  const { data: stats, isLoading: statsLoading } = useQuery({
    queryKey: ['telemetry-stats'],
    queryFn: () => telemetryApi.getStats(7),
  })

  const { data: knowledge } = useQuery({
    queryKey: ['knowledge-count'],
    queryFn: () => knowledgeApi.getAll({ isValid: true }),
  })

  const { data: conversations } = useQuery({
    queryKey: ['conversations-recent'],
    queryFn: () => conversationsApi.getAll(1, 5),
  })

  if (statsLoading) {
    return <div className="text-gray-400">Loading...</div>
  }

  return (
    <div>
      <h1 className="text-2xl font-bold mb-6">Dashboard</h1>

      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mb-8">
        <StatCard
          title="Total Interactions (7d)"
          value={stats?.totalInteractions ?? 0}
          subtitle={`${stats?.successfulInteractions ?? 0} successful`}
        />
        <StatCard
          title="Total Tokens (7d)"
          value={stats?.totalTokens ?? 0}
          subtitle={`${stats?.totalPromptTokens ?? 0} prompt / ${stats?.totalCompletionTokens ?? 0} completion`}
        />
        <StatCard
          title="Avg Latency"
          value={`${Math.round(stats?.averageLatencyMs ?? 0)}ms`}
          subtitle="per interaction"
        />
        <StatCard
          title="Knowledge Facts"
          value={knowledge?.length ?? 0}
          subtitle="active entries"
        />
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <div className="bg-gray-800 rounded-lg p-4">
          <h2 className="text-lg font-semibold mb-4">Recent Conversations</h2>
          {conversations?.items.length === 0 ? (
            <p className="text-gray-400">No conversations yet</p>
          ) : (
            <ul className="space-y-2">
              {conversations?.items.map((c) => (
                <li key={c.id} className="flex justify-between items-center py-2 border-b border-gray-700">
                  <span className="truncate">{c.title || 'Untitled'}</span>
                  <span className="text-sm text-gray-400">{c.messageCount} messages</span>
                </li>
              ))}
            </ul>
          )}
        </div>

        <div className="bg-gray-800 rounded-lg p-4">
          <h2 className="text-lg font-semibold mb-4">Tool Calls (7d)</h2>
          <div className="space-y-2">
            <div className="flex justify-between">
              <span>Total</span>
              <span>{stats?.totalToolCalls ?? 0}</span>
            </div>
            <div className="flex justify-between text-green-400">
              <span>Successful</span>
              <span>{stats?.successfulToolCalls ?? 0}</span>
            </div>
            <div className="flex justify-between text-red-400">
              <span>Failed</span>
              <span>{stats?.failedToolCalls ?? 0}</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}

function StatCard({ title, value, subtitle }: { title: string; value: string | number; subtitle: string }) {
  return (
    <div className="bg-gray-800 rounded-lg p-4">
      <div className="text-sm text-gray-400">{title}</div>
      <div className="text-2xl font-bold mt-1">{value}</div>
      <div className="text-xs text-gray-500 mt-1">{subtitle}</div>
    </div>
  )
}
