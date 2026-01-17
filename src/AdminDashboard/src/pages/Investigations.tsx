import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { investigationsApi } from '../api/client'

export default function Investigations() {
  const [page, setPage] = useState(1)
  const [filter, setFilter] = useState<boolean | undefined>(undefined)
  const pageSize = 20

  const { data, isLoading } = useQuery({
    queryKey: ['investigations', page, filter],
    queryFn: () => investigationsApi.getAll({ page, pageSize, resolved: filter }),
  })

  const totalPages = data ? Math.ceil(data.totalCount / pageSize) : 0

  return (
    <div>
      <div className="flex justify-between items-center mb-6">
        <h1 className="text-2xl font-bold">Investigations</h1>
        <div className="flex gap-2">
          <button
            onClick={() => setFilter(undefined)}
            className={`px-3 py-1 rounded ${filter === undefined ? 'bg-blue-600' : 'bg-gray-700'}`}
          >
            All
          </button>
          <button
            onClick={() => setFilter(false)}
            className={`px-3 py-1 rounded ${filter === false ? 'bg-yellow-600' : 'bg-gray-700'}`}
          >
            Active
          </button>
          <button
            onClick={() => setFilter(true)}
            className={`px-3 py-1 rounded ${filter === true ? 'bg-green-600' : 'bg-gray-700'}`}
          >
            Resolved
          </button>
        </div>
      </div>

      {isLoading ? (
        <div className="text-gray-400">Loading...</div>
      ) : data?.items.length === 0 ? (
        <div className="text-gray-400">No investigations yet</div>
      ) : (
        <>
          <div className="bg-gray-800 rounded-lg overflow-hidden">
            <table className="w-full">
              <thead className="bg-gray-700">
                <tr>
                  <th className="px-4 py-3 text-left">Status</th>
                  <th className="px-4 py-3 text-left">Trigger</th>
                  <th className="px-4 py-3 text-left">Steps</th>
                  <th className="px-4 py-3 text-left">Started</th>
                  <th className="px-4 py-3 text-left">Resolution</th>
                </tr>
              </thead>
              <tbody>
                {data?.items.map((inv) => (
                  <tr key={inv.id} className="border-t border-gray-700 hover:bg-gray-750">
                    <td className="px-4 py-3">
                      <span
                        className={`px-2 py-1 rounded text-xs font-medium ${
                          inv.resolved
                            ? 'bg-green-900/50 text-green-400'
                            : 'bg-yellow-900/50 text-yellow-400'
                        }`}
                      >
                        {inv.resolved ? 'Resolved' : 'Active'}
                      </span>
                    </td>
                    <td className="px-4 py-3">
                      <Link to={`/investigations/${inv.id}`} className="text-blue-400 hover:underline">
                        {inv.trigger.length > 80 ? inv.trigger.slice(0, 77) + '...' : inv.trigger}
                      </Link>
                    </td>
                    <td className="px-4 py-3">{inv.stepCount}</td>
                    <td className="px-4 py-3 text-gray-400">
                      {new Date(inv.startedAt).toLocaleString()}
                    </td>
                    <td className="px-4 py-3 text-gray-400">
                      {inv.resolution
                        ? inv.resolution.length > 50
                          ? inv.resolution.slice(0, 47) + '...'
                          : inv.resolution
                        : '-'}
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
