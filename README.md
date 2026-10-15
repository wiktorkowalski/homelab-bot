# HomeLabBot

Discord bot for the homelab: chat with tools (Docker, Prometheus, Loki, Grafana, Alertmanager, TrueNAS, MikroTik,
Home Assistant, ntfy), scheduled healthchecks and notifications, an MCP server, and an admin dashboard.
The bot's own LLM is Claude through the Claude API (`Bot__AnthropicApiKey`, `Bot__AnthropicModel`).

## Claude Code on the host (`/claude`, `ask_claude`)

The bot can hand work to the Claude Code CLI on the homelab host. The bot gets no shell: one SSH key that can only
start `/home/ubuntu/bin/claude-run`, which runs `claude -p` as user `ubuntu` with the user's own config, permission
mode, MCP servers and `CLAUDE.md`. The runs use the owner's Claude subscription, not the API key.

- `/claude <message>` (owner only). In a guild channel it opens a thread; in the owner DM it answers in the DM.
  The message goes to Claude unchanged, with no plan or confirm step.
- Follow-ups: an owner message in a `/claude` thread, or a Discord Reply to a Claude result in the DM, resumes the
  same Claude session. Messages of other users in such a thread are ignored.
- `AskClaude` tool: the bot's LLM can delegate multi-step work in a chat turn. The owner approves each call with a
  button. It returns at once; the result arrives as a separate message in the same chat. Scheduled runs never get
  the tool, and it is not on the MCP surface.
- One run at a time. Time limit: 30 minutes for owner runs, 10 for runs the bot's LLM starts. A stopped or
  interrupted run can be resumed.
- The result is redacted (tokens, `NAME=value` secrets, `Authorization` values, URL credentials, private keys)
  before it goes to Discord. The footer shows duration, turns, the notional API price (not billed) and the session id.
- Every run is stored in the `ClaudeRuns` table.

### Configuration

| Key | Default | Meaning |
| --- | --- | --- |
| `ClaudeRunner__Enabled` | `false` | Feature flag. Off: `/claude` says disabled, the tool is not registered. |
| `ClaudeRunner__Host` | `host.docker.internal` | SSH host (the docker host, through `extra_hosts`). |
| `ClaudeRunner__User` | `ubuntu` | SSH user. |
| `ClaudeRunner__KeyPath` | `/app/ssh/id_ed25519` | Private key of the bot, mounted read-only. |
| `ClaudeRunner__KnownHostsPath` | `/app/ssh/known_hosts` | Host key of the docker host. |
| `ClaudeRunner__OwnerTimeoutMinutes` | `30` | Time limit of owner runs. |
| `ClaudeRunner__LlmTimeoutMinutes` | `10` | Time limit of `AskClaude` runs. |
| `ClaudeRunner__RecordedResultMaxChars` | `8000` | Cap of the result copy in the owner DM history. |

### Host setup

Do these steps before `ClaudeRunner__Enabled=true` reaches the host `.env`.

1. Generate a key pair for the bot (no passphrase) in `/home/ubuntu/docker/homelab-bot/ssh/`, private key mode 600:

   ```sh
   mkdir -p ~/docker/homelab-bot/ssh && chmod 700 ~/docker/homelab-bot/ssh
   ssh-keygen -q -t ed25519 -N "" -C homelab-bot -f ~/docker/homelab-bot/ssh/id_ed25519
   ```

2. Add one line to `~/.ssh/authorized_keys` of `ubuntu`. Do not touch other lines:

   ```
   restrict,command="/home/ubuntu/bin/claude-run" ssh-ed25519 <public key> homelab-bot
   ```

3. Create `/home/ubuntu/bin/claude-run`, mode 755:

   ```sh
   #!/bin/sh
   # $SSH_ORIGINAL_COMMAND = "new <uuid>" or "resume <uuid>"; stdin = the message
   set -eu
   cmd=${SSH_ORIGINAL_COMMAND:-}
   mode=${cmd%% *}
   id=${cmd#* }
   echo "$id" | grep -Eq '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' || { echo "bad id" >&2; exit 64; }
   cd /home/ubuntu/docker
   export CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1
   case "$mode" in
     new)    exec /home/ubuntu/.local/bin/claude -p --output-format json --session-id "$id" ;;
     resume) exec /home/ubuntu/.local/bin/claude -p --output-format json --resume "$id" ;;
     *)      echo "bad mode" >&2; exit 64 ;;
   esac
   ```

   Add no other `claude` flags: each one moves the behavior away from a manual run. Do not put the Claude API key
   into the environment of `ubuntu`: the CLI would bill the runs to it.

4. Write the host key next to the private key:

   ```sh
   echo "host.docker.internal $(cut -d' ' -f1,2 /etc/ssh/ssh_host_ed25519_key.pub)" > ~/docker/homelab-bot/ssh/known_hosts
   ```

5. In `/home/ubuntu/docker/homelab-bot/compose.yml` (not in this repo), mount the files and map the host:

   ```yaml
       volumes:
         - ./ssh/id_ed25519:/app/ssh/id_ed25519:ro
         - ./ssh/known_hosts:/app/ssh/known_hosts:ro
       extra_hosts:
         - "host.docker.internal:host-gateway"
   ```

   Then set `ClaudeRunner__Enabled=true` in the `.env` next to it.

Check from inside the container:

```sh
echo 'Reply with one word: OK' | docker exec -i homelab-bot ssh -i /app/ssh/id_ed25519 -o BatchMode=yes \
  -o StrictHostKeyChecking=yes -o UserKnownHostsFile=/app/ssh/known_hosts ubuntu@host.docker.internal "new $(uuidgen | tr A-Z a-z)"
```
