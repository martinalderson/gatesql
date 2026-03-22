#!/bin/bash
set -e

if [ -z "$PROMPT" ]; then
    echo "Error: PROMPT environment variable is required" >&2
    exit 1
fi

# Write auth.json if provided
if [ -n "$AUTH_JSON_BASE64" ]; then
    mkdir -p ~/.local/share/opencode
    echo "$AUTH_JSON_BASE64" | base64 -d > ~/.local/share/opencode/auth.json
fi

# Build model flag
MODEL_FLAG=""
if [ -n "$MODEL" ]; then
    MODEL_FLAG="-m $MODEL"
fi

# If GateSQL connection string provided, make psql available with it
if [ -n "$DATABASE_URL" ]; then
    # Replace localhost with host.docker.internal for Docker networking
    DATABASE_URL=$(echo "$DATABASE_URL" | sed 's/@localhost:/@host.docker.internal:/g')
    PROMPT="You have psql available. Connection string: $DATABASE_URL

$PROMPT"
fi

echo "Starting opencode task..."
opencode run $MODEL_FLAG "$PROMPT"
