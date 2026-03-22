# OpenCode + GateSQL Demo

Run an OpenCode AI agent against your database through GateSQL.

## Build

```bash
docker build -t opencode-gatesql demo/opencode/
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
  opencode-gatesql
```

Watch the queries appear in the GateSQL dashboard at http://localhost:8080.
