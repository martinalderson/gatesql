# OpenCode AI agent with psql for querying through GateSQL
FROM node:22-alpine AS builder
RUN npm install -g opencode-ai@latest

FROM node:22-alpine

RUN apk add --no-cache bash git curl postgresql-client

COPY --from=builder /usr/local/lib/node_modules /usr/local/lib/node_modules
COPY --from=builder /usr/local/bin /usr/local/bin

# Replace the glibc binary with the musl one (Alpine uses musl)
RUN ARCH=$(uname -m) && \
    if [ "$ARCH" = "aarch64" ]; then \
      cp -f /usr/local/lib/node_modules/opencode-ai/node_modules/opencode-linux-arm64-musl/bin/opencode \
        /usr/local/lib/node_modules/opencode-ai/bin/.opencode; \
    elif [ "$ARCH" = "x86_64" ]; then \
      cp -f /usr/local/lib/node_modules/opencode-ai/node_modules/opencode-linux-x64-musl/bin/opencode \
        /usr/local/lib/node_modules/opencode-ai/bin/.opencode; \
    fi

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

WORKDIR /workspace
ENTRYPOINT ["/entrypoint.sh"]
