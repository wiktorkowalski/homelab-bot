import { useParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { investigationsApi } from '../api/client'

export default function InvestigationDetail() {
  const { id } = useParams<{ id: string }>()

  const { data, isLoading, error } = useQuery({
    queryKey: ['investigation', id],
    queryFn: () => investigationsApi.getById(Number(id)),
    enabled: !!id,
  })

  if (isLoading) return <div className="text-gray-400">Loading...</div>
  if (error) return <div className="text-red-400">Error loading investigation</div>
  if (!data) return <div className="text-gray-400">Investigation not found</div>

  return (
    <div>
      <Link to="/investigations" className="text-blue-400 hover:underline mb-4 block">
        &larr; Back to Investigations
      </Link>

      <div className="flex justify-between items-start mb-6">
        <div>
          <div className="flex items-center gap-3 mb-2">
            <h1 className="text-2xl font-bold">Investigation #{data.id}</h1>
            <span
              className={`px-2 py-1 rounded text-xs font-medium ${
                data.resolved
                  ? 'bg-green-900/50 text-green-400'
                  : 'bg-yellow-900/50 text-yellow-400'
              }`}
            >
              {data.resolved ? 'Resolved' : 'Active'}
            </span>
          </div>
          <div className="text-gray-400 text-sm">
            Thread ID: <span className="font-mono">{data.threadId}</span>
          </div>
        </div>
        <div className="text-right text-sm text-gray-400">
          Started: {new Date(data.startedAt).toLocaleString()}
        </div>
      </div>

      <div className="bg-gray-800 rounded-lg p-4 mb-6">
        <h2 className="text-sm font-semibold text-gray-400 mb-2">Trigger</h2>
        <p className="whitespace-pre-wrap">{data.trigger}</p>
      </div>

      {data.resolution && (
        <div className="bg-green-900/20 border border-green-800 rounded-lg p-4 mb-6">
          <h2 className="text-sm font-semibold text-green-400 mb-2">Resolution</h2>
          <p className="whitespace-pre-wrap">{data.resolution}</p>
        </div>
      )}

      <h2 className="text-xl font-semibold mb-4">
        Diagnostic Steps ({data.steps.length})
      </h2>

      {data.steps.length === 0 ? (
        <div className="text-gray-400">No diagnostic steps recorded</div>
      ) : (
        <div className="space-y-3">
          {data.steps.map((step, index) => (
            <div key={step.id} className="bg-gray-800 rounded-lg p-4">
              <div className="flex justify-between items-start mb-2">
                <div className="flex items-center gap-2">
                  <span className="text-gray-500 font-mono text-sm">#{index + 1}</span>
                  <span className="font-medium">{step.action}</span>
                  {step.plugin && (
                    <span className="px-2 py-0.5 bg-blue-900/50 text-blue-400 text-xs rounded">
                      {step.plugin}
                    </span>
                  )}
                </div>
                <span className="text-xs text-gray-500">
                  {new Date(step.timestamp).toLocaleString()}
                </span>
              </div>
              {step.resultSummary && (
                <div className="mt-2 text-sm text-gray-400 whitespace-pre-wrap bg-gray-900/50 p-2 rounded">
                  {step.resultSummary}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
