import { useParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { telemetryApi } from '../api/client'

export default function TelemetryDetail() {
  const { id } = useParams<{ id: string }>()

  const { data, isLoading, error } = useQuery({
    queryKey: ['telemetry-detail', id],
    queryFn: () => telemetryApi.getById(parseInt(id!)),
    enabled: !!id,
  })

  if (isLoading) return <div className="text-gray-400">Loading...</div>
  if (error) return <div className="text-red-400">Error loading interaction</div>
  if (!data) return <div className="text-gray-400">Interaction not found</div>

  return (
    <div>
      <Link to="/telemetry" className="text-blue-400 hover:underline mb-4 block">
        ← Back to Telemetry
      </Link>

      <div className="flex justify-between items-start mb-6">
        <div>
          <h1 className="text-2xl font-bold">Interaction #{data.id}</h1>
          <div className="text-gray-400 text-sm mt-1">Model: {data.model}</div>
        </div>
        <span
          className={`px-3 py-1 rounded ${
            data.success ? 'bg-green-900 text-green-300' : 'bg-red-900 text-red-300'
          }`}
        >
          {data.success ? 'Success' : 'Failed'}
        </span>
      </div>

      <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-6">
        <StatBox label="Prompt Tokens" value={data.promptTokens ?? 0} />
        <StatBox label="Completion Tokens" value={data.completionTokens ?? 0} />
        <StatBox label="Latency" value={`${data.latencyMs}ms`} />
        <StatBox label="Tool Calls" value={data.toolCalls.length} />
      </div>

      <div className="space-y-6">
        <Section title="User Prompt">
          <pre className="whitespace-pre-wrap bg-gray-800 p-4 rounded">{data.userPrompt}</pre>
        </Section>

        {data.response && (
          <Section title="Response">
            <pre className="whitespace-pre-wrap bg-gray-800 p-4 rounded">{data.response}</pre>
          </Section>
        )}

        {data.errorMessage && (
          <Section title="Error">
            <pre className="whitespace-pre-wrap bg-red-900/30 p-4 rounded text-red-300">
              {data.errorMessage}
            </pre>
          </Section>
        )}

        {data.toolCalls.length > 0 && (
          <Section title="Tool Calls">
            <div className="space-y-4">
              {data.toolCalls.map((tc) => (
                <div key={tc.id} className="bg-gray-800 p-4 rounded">
                  <div className="flex justify-between items-center mb-2">
                    <span className="font-mono text-blue-400">
                      {tc.pluginName}.{tc.functionName}
                    </span>
                    <div className="flex items-center gap-4">
                      <span className="text-gray-400 text-sm">{tc.latencyMs}ms</span>
                      <span
                        className={`px-2 py-1 rounded text-xs ${
                          tc.success ? 'bg-green-900 text-green-300' : 'bg-red-900 text-red-300'
                        }`}
                      >
                        {tc.success ? 'OK' : 'Error'}
                      </span>
                    </div>
                  </div>
                  {tc.argumentsJson && (
                    <div className="mb-2">
                      <div className="text-xs text-gray-500 mb-1">Arguments:</div>
                      <pre className="text-sm bg-gray-900 p-2 rounded overflow-x-auto">
                        {formatJson(tc.argumentsJson)}
                      </pre>
                    </div>
                  )}
                  {tc.resultJson && (
                    <div>
                      <div className="text-xs text-gray-500 mb-1">Result:</div>
                      <pre className="text-sm bg-gray-900 p-2 rounded overflow-x-auto max-h-48">
                        {formatJson(tc.resultJson)}
                      </pre>
                    </div>
                  )}
                  {tc.errorMessage && (
                    <div className="text-red-400 text-sm mt-2">{tc.errorMessage}</div>
                  )}
                </div>
              ))}
            </div>
          </Section>
        )}

        {data.fullMessagesJson && (
          <Section title="Full Message History">
            <pre className="whitespace-pre-wrap bg-gray-800 p-4 rounded text-sm max-h-96 overflow-y-auto">
              {formatJson(data.fullMessagesJson)}
            </pre>
          </Section>
        )}
      </div>
    </div>
  )
}

function StatBox({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="bg-gray-800 p-4 rounded">
      <div className="text-xs text-gray-400">{label}</div>
      <div className="text-xl font-bold mt-1">{value}</div>
    </div>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <h2 className="text-lg font-semibold mb-2">{title}</h2>
      {children}
    </div>
  )
}

function formatJson(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2)
  } catch {
    return json
  }
}
