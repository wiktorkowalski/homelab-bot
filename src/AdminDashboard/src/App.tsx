import { Routes, Route, NavLink } from 'react-router-dom'
import Dashboard from './pages/Dashboard'
import Knowledge from './pages/Knowledge'
import Conversations from './pages/Conversations'
import ConversationDetail from './pages/ConversationDetail'
import Telemetry from './pages/Telemetry'
import TelemetryDetail from './pages/TelemetryDetail'
import Investigations from './pages/Investigations'
import InvestigationDetail from './pages/InvestigationDetail'

function App() {
  return (
    <div className="min-h-screen flex">
      <aside className="w-64 bg-gray-800 p-4">
        <h1 className="text-xl font-bold mb-6">HomelabBot Admin</h1>
        <nav className="space-y-2">
          <NavLink
            to="/"
            className={({ isActive }) =>
              `block px-4 py-2 rounded ${isActive ? 'bg-blue-600' : 'hover:bg-gray-700'}`
            }
          >
            Dashboard
          </NavLink>
          <NavLink
            to="/knowledge"
            className={({ isActive }) =>
              `block px-4 py-2 rounded ${isActive ? 'bg-blue-600' : 'hover:bg-gray-700'}`
            }
          >
            Knowledge
          </NavLink>
          <NavLink
            to="/conversations"
            className={({ isActive }) =>
              `block px-4 py-2 rounded ${isActive ? 'bg-blue-600' : 'hover:bg-gray-700'}`
            }
          >
            Conversations
          </NavLink>
          <NavLink
            to="/telemetry"
            className={({ isActive }) =>
              `block px-4 py-2 rounded ${isActive ? 'bg-blue-600' : 'hover:bg-gray-700'}`
            }
          >
            Telemetry
          </NavLink>
          <NavLink
            to="/investigations"
            className={({ isActive }) =>
              `block px-4 py-2 rounded ${isActive ? 'bg-blue-600' : 'hover:bg-gray-700'}`
            }
          >
            Investigations
          </NavLink>
        </nav>
      </aside>
      <main className="flex-1 p-6">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/knowledge" element={<Knowledge />} />
          <Route path="/conversations" element={<Conversations />} />
          <Route path="/conversations/:threadId" element={<ConversationDetail />} />
          <Route path="/telemetry" element={<Telemetry />} />
          <Route path="/telemetry/:id" element={<TelemetryDetail />} />
          <Route path="/investigations" element={<Investigations />} />
          <Route path="/investigations/:id" element={<InvestigationDetail />} />
        </Routes>
      </main>
    </div>
  )
}

export default App
