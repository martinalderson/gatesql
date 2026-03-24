# OpenCode + GateSQL Demo

Run an OpenCode AI agent against your database through GateSQL.

## Prerequisites

- GateSQL running (via Docker or from source) with a database configured
- An API key (shown in the GateSQL welcome banner or your config)
- OpenCode auth (optional — OpenCode includes free models out of the box)

## Build

```bash
docker build -t gatesql/opencode-demo demo/opencode/
```

## Usage

```bash
# 1. Create a GateSQL session
RESPONSE=$(curl -s -X POST http://localhost:8080/api/sessions \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: YOUR_API_KEY" \
  -d '{"agentId":"opencode","task":"analyze data","queryBudget":50,"readOnly":true}')

CONNECTION_STRING=$(echo $RESPONSE | jq -r .connectionString)

# 2. Run OpenCode with the connection
docker run --rm \
  --add-host=host.docker.internal:host-gateway \
  -e DATABASE_URL="$CONNECTION_STRING" \
  -e AUTH_JSON_BASE64="$(base64 -w0 ~/.local/share/opencode/auth.json)" \
  -e PROMPT="Analyze the top customers by revenue and summarize findings." \
  gatesql/opencode-demo
```

Watch the queries appear in the GateSQL dashboard at http://localhost:8080.

## Environment variables

| Variable | Required | Description |
|---|---|---|
| `PROMPT` | Yes | The task for the agent |
| `DATABASE_URL` | Yes | GateSQL connection string (from session creation response) |
| `AUTH_JSON_BASE64` | No | Base64-encoded OpenCode auth.json for custom providers (`base64 -w0 ~/.local/share/opencode/auth.json`) |
| `MODEL` | No | Model to use (e.g. `anthropic/claude-sonnet-4-20250514`) |

## How it works

1. The entrypoint rewrites `localhost` to `host.docker.internal` in the connection string so Docker networking works
2. Injects "You have psql available" + the connection string into the prompt
3. OpenCode discovers the schema, writes queries with purpose comments, and gets results — all logged in the GateSQL dashboard
