const BASE_URL = '/api'

async function fetchApi<T>(endpoint: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE_URL}${endpoint}`, {
    headers: {
      'Content-Type': 'application/json',
      ...options?.headers,
    },
    ...options,
  })

  if (!res.ok) {
    throw new Error(`API error: ${res.status} ${res.statusText}`)
  }

  return res.json()
}

// Knowledge
export interface Knowledge {
  id: number
  topic: string
  fact: string
  context?: string
  confidence: number
  source: string
  isValid: boolean
  lastVerified?: string
  createdAt: string
  lastUsed?: string
}

export interface CreateKnowledgeRequest {
  topic: string
  fact: string
  context?: string
  confidence?: number
  source?: string
}

export interface UpdateKnowledgeRequest {
  topic?: string
  fact?: string
  context?: string
  confidence?: number
  isValid?: boolean
}

export const knowledgeApi = {
  getAll: (params?: { topic?: string; isValid?: boolean }) => {
    const query = new URLSearchParams()
    if (params?.topic) query.set('topic', params.topic)
    if (params?.isValid !== undefined) query.set('isValid', String(params.isValid))
    return fetchApi<Knowledge[]>(`/knowledge?${query}`)
  },
  getById: (id: number) => fetchApi<Knowledge>(`/knowledge/${id}`),
  create: (data: CreateKnowledgeRequest) =>
    fetchApi<Knowledge>('/knowledge', { method: 'POST', body: JSON.stringify(data) }),
  update: (id: number, data: UpdateKnowledgeRequest) =>
    fetchApi<Knowledge>(`/knowledge/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
  delete: (id: number) => fetchApi<void>(`/knowledge/${id}`, { method: 'DELETE' }),
}

// Conversations
export interface Conversation {
  id: number
  threadId: string
  title?: string
  createdAt: string
  lastMessageAt?: string
  messageCount: number
}

export interface Message {
  id: number
  role: string
  content: string
  timestamp: string
}

export interface ConversationDetail extends Conversation {
  messages: Message[]
}

export interface PagedResult<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
}

export const conversationsApi = {
  getAll: (page = 1, pageSize = 20) =>
    fetchApi<PagedResult<Conversation>>(`/conversations?page=${page}&pageSize=${pageSize}`),
  getByThreadId: (threadId: string) =>
    fetchApi<ConversationDetail>(`/conversations/${threadId}`),
}

// Telemetry
export interface LlmInteraction {
  id: number
  threadId?: string
  model: string
  userPrompt: string
  success: boolean
  promptTokens?: number
  completionTokens?: number
  latencyMs: number
  timestamp: string
  toolCallCount: number
}

export interface ToolCall {
  id: number
  pluginName: string
  functionName: string
  argumentsJson?: string
  resultJson?: string
  success: boolean
  errorMessage?: string
  latencyMs: number
  timestamp: string
}

export interface LlmInteractionDetail extends LlmInteraction {
  conversationId?: number
  fullMessagesJson?: string
  response?: string
  errorMessage?: string
  toolCalls: ToolCall[]
}

export interface TelemetryStats {
  days: number
  totalInteractions: number
  successfulInteractions: number
  failedInteractions: number
  totalPromptTokens: number
  totalCompletionTokens: number
  totalTokens: number
  averageLatencyMs: number
  totalToolCalls: number
  successfulToolCalls: number
  failedToolCalls: number
}

export const telemetryApi = {
  getAll: (params?: { page?: number; pageSize?: number; threadId?: string; success?: boolean }) => {
    const query = new URLSearchParams()
    if (params?.page) query.set('page', String(params.page))
    if (params?.pageSize) query.set('pageSize', String(params.pageSize))
    if (params?.threadId) query.set('threadId', params.threadId)
    if (params?.success !== undefined) query.set('success', String(params.success))
    return fetchApi<PagedResult<LlmInteraction>>(`/telemetry?${query}`)
  },
  getById: (id: number) => fetchApi<LlmInteractionDetail>(`/telemetry/${id}`),
  getStats: (days = 7) => fetchApi<TelemetryStats>(`/telemetry/stats?days=${days}`),
}

// Investigations
export interface Investigation {
  id: number
  threadId: string
  trigger: string
  startedAt: string
  resolved: boolean
  resolution?: string
  stepCount: number
}

export interface InvestigationStep {
  id: number
  action: string
  plugin?: string
  resultSummary?: string
  timestamp: string
}

export interface InvestigationDetail extends Omit<Investigation, 'stepCount'> {
  steps: InvestigationStep[]
}

export const investigationsApi = {
  getAll: (params?: { page?: number; pageSize?: number; resolved?: boolean }) => {
    const query = new URLSearchParams()
    if (params?.page) query.set('page', String(params.page))
    if (params?.pageSize) query.set('pageSize', String(params.pageSize))
    if (params?.resolved !== undefined) query.set('resolved', String(params.resolved))
    return fetchApi<PagedResult<Investigation>>(`/investigations?${query}`)
  },
  getById: (id: number) => fetchApi<InvestigationDetail>(`/investigations/${id}`),
  resolve: (id: number, resolution: string) =>
    fetchApi<InvestigationDetail>(`/investigations/${id}/resolve`, {
      method: 'POST',
      body: JSON.stringify({ resolution }),
    }),
}
