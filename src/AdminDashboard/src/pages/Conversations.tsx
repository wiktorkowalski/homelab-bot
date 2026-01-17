import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { conversationsApi } from '../api/client'

export default function Conversations() {
  const [page, setPage] = useState(1)
  const pageSize = 20

  const { data, isLoading } = useQuery({
    queryKey: ['conversations', page],
    queryFn: () => conversationsApi.getAll(page, pageSize),
  })

  const totalPages = data ? Math.ceil(data.totalCount / pageSize) : 0

  return (
    <div>
      <h1 className="text-2xl font-bold mb-6">Conversations</h1>

      {isLoading ? (
        <div className="text-gray-400">Loading...</div>
      ) : data?.items.length === 0 ? (
        <div className="text-gray-400">No conversations yet</div>
      ) : (
        <>
          <div className="bg-gray-800 rounded-lg overflow-hidden">
            <table className="w-full">
              <thead className="bg-gray-700">
                <tr>
                  <th className="px-4 py-3 text-left">Title</th>
                  <th className="px-4 py-3 text-left">Thread ID</th>
                  <th className="px-4 py-3 text-left">Messages</th>
                  <th className="px-4 py-3 text-left">Created</th>
                  <th className="px-4 py-3 text-left">Last Activity</th>
                </tr>
              </thead>
              <tbody>
                {data?.items.map((c) => (
                  <tr key={c.id} className="border-t border-gray-700 hover:bg-gray-750">
                    <td className="px-4 py-3">
                      <Link to={`/conversations/${c.threadId}`} className="text-blue-400 hover:underline">
                        {c.title || 'Untitled'}
                      </Link>
                    </td>
                    <td className="px-4 py-3 text-gray-400 font-mono text-sm">{c.threadId}</td>
                    <td className="px-4 py-3">{c.messageCount}</td>
                    <td className="px-4 py-3 text-gray-400">
                      {new Date(c.createdAt).toLocaleString()}
                    </td>
                    <td className="px-4 py-3 text-gray-400">
                      {c.lastMessageAt ? new Date(c.lastMessageAt).toLocaleString() : '-'}
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
