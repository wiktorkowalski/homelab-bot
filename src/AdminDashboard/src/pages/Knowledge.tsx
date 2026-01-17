import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { knowledgeApi } from '../api/client'
import type { Knowledge, CreateKnowledgeRequest, UpdateKnowledgeRequest } from '../api/client'

export default function KnowledgePage() {
  const [filter, setFilter] = useState({ topic: '', showInvalid: false })
  const [editingId, setEditingId] = useState<number | null>(null)
  const [showCreate, setShowCreate] = useState(false)

  const queryClient = useQueryClient()

  const { data: items, isLoading } = useQuery({
    queryKey: ['knowledge', filter],
    queryFn: () => knowledgeApi.getAll({
      topic: filter.topic || undefined,
      isValid: filter.showInvalid ? undefined : true,
    }),
  })

  const createMutation = useMutation({
    mutationFn: knowledgeApi.create,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['knowledge'] })
      setShowCreate(false)
    },
  })

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: number; data: UpdateKnowledgeRequest }) =>
      knowledgeApi.update(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['knowledge'] })
      setEditingId(null)
    },
  })

  const deleteMutation = useMutation({
    mutationFn: knowledgeApi.delete,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['knowledge'] }),
  })

  return (
    <div>
      <div className="flex justify-between items-center mb-6">
        <h1 className="text-2xl font-bold">Knowledge Base</h1>
        <button
          onClick={() => setShowCreate(true)}
          className="bg-blue-600 hover:bg-blue-700 px-4 py-2 rounded"
        >
          Add Knowledge
        </button>
      </div>

      <div className="flex gap-4 mb-4">
        <input
          type="text"
          placeholder="Filter by topic..."
          value={filter.topic}
          onChange={(e) => setFilter({ ...filter, topic: e.target.value })}
          className="bg-gray-800 px-4 py-2 rounded flex-1"
        />
        <label className="flex items-center gap-2">
          <input
            type="checkbox"
            checked={filter.showInvalid}
            onChange={(e) => setFilter({ ...filter, showInvalid: e.target.checked })}
          />
          Show invalid
        </label>
      </div>

      {showCreate && (
        <KnowledgeForm
          onSubmit={(data) => createMutation.mutate(data as CreateKnowledgeRequest)}
          onCancel={() => setShowCreate(false)}
          isLoading={createMutation.isPending}
        />
      )}

      {isLoading ? (
        <div className="text-gray-400">Loading...</div>
      ) : items?.length === 0 ? (
        <div className="text-gray-400">No knowledge entries found</div>
      ) : (
        <div className="space-y-4">
          {items?.map((item) => (
            <div key={item.id} className="bg-gray-800 rounded-lg p-4">
              {editingId === item.id ? (
                <KnowledgeForm
                  initialData={item}
                  onSubmit={(data) => updateMutation.mutate({ id: item.id, data })}
                  onCancel={() => setEditingId(null)}
                  isLoading={updateMutation.isPending}
                />
              ) : (
                <KnowledgeItem
                  item={item}
                  onEdit={() => setEditingId(item.id)}
                  onDelete={() => deleteMutation.mutate(item.id)}
                />
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function KnowledgeItem({
  item,
  onEdit,
  onDelete,
}: {
  item: Knowledge
  onEdit: () => void
  onDelete: () => void
}) {
  return (
    <div>
      <div className="flex justify-between items-start">
        <div>
          <span className="text-blue-400 text-sm">{item.topic}</span>
          {!item.isValid && <span className="ml-2 text-red-400 text-xs">(invalid)</span>}
        </div>
        <div className="flex gap-2">
          <button onClick={onEdit} className="text-gray-400 hover:text-white text-sm">
            Edit
          </button>
          <button onClick={onDelete} className="text-red-400 hover:text-red-300 text-sm">
            Delete
          </button>
        </div>
      </div>
      <p className="mt-2">{item.fact}</p>
      {item.context && <p className="text-sm text-gray-400 mt-1">{item.context}</p>}
      <div className="flex gap-4 mt-2 text-xs text-gray-500">
        <span>Confidence: {(item.confidence * 100).toFixed(0)}%</span>
        <span>Source: {item.source}</span>
        <span>Created: {new Date(item.createdAt).toLocaleDateString()}</span>
      </div>
    </div>
  )
}

function KnowledgeForm({
  initialData,
  onSubmit,
  onCancel,
  isLoading,
}: {
  initialData?: Knowledge
  onSubmit: (data: CreateKnowledgeRequest | UpdateKnowledgeRequest) => void
  onCancel: () => void
  isLoading: boolean
}) {
  const [form, setForm] = useState({
    topic: initialData?.topic ?? '',
    fact: initialData?.fact ?? '',
    context: initialData?.context ?? '',
    confidence: initialData?.confidence ?? 0.8,
  })

  return (
    <div className="space-y-4">
      <input
        type="text"
        placeholder="Topic"
        value={form.topic}
        onChange={(e) => setForm({ ...form, topic: e.target.value })}
        className="w-full bg-gray-700 px-4 py-2 rounded"
      />
      <textarea
        placeholder="Fact"
        value={form.fact}
        onChange={(e) => setForm({ ...form, fact: e.target.value })}
        className="w-full bg-gray-700 px-4 py-2 rounded h-24"
      />
      <input
        type="text"
        placeholder="Context (optional)"
        value={form.context}
        onChange={(e) => setForm({ ...form, context: e.target.value })}
        className="w-full bg-gray-700 px-4 py-2 rounded"
      />
      <div className="flex items-center gap-2">
        <label>Confidence:</label>
        <input
          type="range"
          min="0"
          max="1"
          step="0.1"
          value={form.confidence}
          onChange={(e) => setForm({ ...form, confidence: parseFloat(e.target.value) })}
          className="flex-1"
        />
        <span>{(form.confidence * 100).toFixed(0)}%</span>
      </div>
      <div className="flex gap-2">
        <button
          onClick={() => onSubmit(form)}
          disabled={isLoading || !form.topic || !form.fact}
          className="bg-blue-600 hover:bg-blue-700 disabled:opacity-50 px-4 py-2 rounded"
        >
          {isLoading ? 'Saving...' : 'Save'}
        </button>
        <button onClick={onCancel} className="bg-gray-700 hover:bg-gray-600 px-4 py-2 rounded">
          Cancel
        </button>
      </div>
    </div>
  )
}
