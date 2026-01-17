import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { telemetryApi } from '../api/client'

export default function Telemetry() {
  const [page, setPage] = useState(1)
  const [filter, setFilter] = useState<{ success?: boolean }>({})
  const pageSize = 20

  const { data, isLoading } = useQuery({
    queryKey: ['telemetry', page, filter],
    queryFn: () => telemetryApi.getAll({ page, pageSize, ...filter }),
  })

  const totalPages = data ? Math.ceil(data.totalCount / pageSize) : 0

  return (
    <div>
      <h1 className="text-2xl font-bold mb-6">LLM Telemetry</h1>

      <div className="flex gap-4 mb-4">
        <select
          value={filter.success === undefined ? '' : String(filter.success)}
          onChange={(e) => {
            const val = e.target.value
            setFilter({
              success: val === '' ? undefined : val === 'true',
            })
            setPage(1)
          }}
          className="bg-gray-800 px-4 py-2 rounded"
        >
          <option value="">All</option>
          <option value="true">Success</option>
          <option value="false">Failed</option>
        </select>
      </div>

      {isLoading ? (
        <div className="text-gray-400">Loading...</div>
      ) : data?.items.length === 0 ? (
        <div className="text-gray-400">No telemetry data yet</div>
      ) : (
        <>
          <div className="bg-gray-800 rounded-lg overflow-hidden">
            <table className="w-full">
              <thead className="bg-gray-700">
                <tr>
                  <th className="px-4 py-3 text-left">ID</th>
                  <th className="px-4 py-3 text-left">Prompt</th>
                  <th className="px-4 py-3 text-left">Status</th>
                  <th className="px-4 py-3 text-left">Tokens</th>
                  <th className="px-4 py-3 text-left">Latency</th>
                  <th className="px-4 py-3 text-left">Tools</th>
                  <th className="px-4 py-3 text-left">Time</th>
                </tr>
              </thead>
              <tbody>
                {data?.items.map((item) => (
                  <tr key={item.id} className="border-t border-gray-700 hover:bg-gray-750">
                    <td className="px-4 py-3">
                      <Link to={`/telemetry/${item.id}`} className="text-blue-400 hover:underline">
                        #{item.id}
                      </Link>
                    </td>
                    <td className="px-4 py-3 max-w-xs truncate" title={item.userPrompt}>
                      {item.userPrompt}
                    </td>
                    <td className="px-4 py-3">
                      <span
                        className={`px-2 py-1 rounded text-xs ${
                          item.success ? 'bg-green-900 text-green-300' : 'bg-red-900 text-red-300'
                        }`}
                      >
                        {item.success ? 'Success' : 'Failed'}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-gray-400">
                      {item.promptTokens ?? 0} + {item.completionTokens ?? 0}
                    </td>
                    <td className="px-4 py-3 text-gray-400">{item.latencyMs}ms</td>
                    <td className="px-4 py-3">{item.toolCallCount}</td>
                    <td className="px-4 py-3 text-gray-400 text-sm">
                      {new Date(item.timestamp).toLocaleString()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {totalPages > 1 && (
            <div className="flex justify-center gap-2 mt-4">
              <button
                onClick={() => setPage((p) => Math.max(1, p - 1))}
                disabled={page === 1}
                className="px-4 py-2 bg-gray-800 rounded disabled:opacity-50"
              >
                Previous
              </button>
              <span className="px-4 py-2">
                Page {page} of {totalPages}
              </span>
              <button
                onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                disabled={page === totalPages}
                className="px-4 py-2 bg-gray-800 rounded disabled:opacity-50"
              >
                Next
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}
