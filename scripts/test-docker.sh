#!/bin/bash
set -euo pipefail

# Docker image smoke tests for gatesql
# Tests both the demo image (self-contained) and the main image (needs external PG)

DEMO_IMAGE="${1:-gatesql/gatesql:demo}"
MAIN_IMAGE="${2:-gatesql/gatesql:latest}"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BOLD='\033[1m'
NC='\033[0m'

CONTAINERS=()
NETWORKS=()
PASSED=0
FAILED=0

cleanup() {
    echo ""
    echo -e "${BOLD}Cleaning up...${NC}"
    for c in "${CONTAINERS[@]}"; do
        docker rm -f "$c" 2>/dev/null || true
    done
    for n in "${NETWORKS[@]}"; do
        docker network rm "$n" 2>/dev/null || true
    done
}
trap cleanup EXIT

pass() {
    echo -e "  ${GREEN}PASS${NC} $1"
    PASSED=$((PASSED + 1))
}

fail() {
    echo -e "  ${RED}FAIL${NC} $1"
    FAILED=$((FAILED + 1))
}

wait_for_api() {
    local port=$1
    local api_key=$2
    local timeout=${3:-30}
    for i in $(seq 1 "$timeout"); do
        if curl -sf "http://localhost:${port}/api/sessions" -H "X-Api-Key: ${api_key}" > /dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    return 1
}

create_session() {
    local port=$1
    local api_key=$2
    local body=$3
    curl -sf -X POST "http://localhost:${port}/api/sessions" \
        -H "Content-Type: application/json" \
        -H "X-Api-Key: ${api_key}" \
        -d "$body"
}

for cmd in docker curl jq psql python3; do
    if ! command -v "$cmd" &> /dev/null; then
        echo -e "${RED}ERROR: $cmd is required but not found${NC}"
        exit 1
    fi
done

echo ""
echo -e "${BOLD}Pulling images...${NC}"
docker pull "$DEMO_IMAGE"
docker pull "$MAIN_IMAGE"

# ─── Demo Image ───────────────────────────────────────────────

test_demo_image() {
    local api_key="pk_demo_key"
    local dashboard_port=8080
    local proxy_port=15432

    echo ""
    echo -e "${BOLD}Testing demo image: ${DEMO_IMAGE}${NC}"
    echo ""

    # Find free ports to avoid conflicts
    dashboard_port=$(python3 -c "import socket; s=socket.socket(); s.bind(('',0)); print(s.getsockname()[1]); s.close()")
    proxy_port=$(python3 -c "import socket; s=socket.socket(); s.bind(('',0)); print(s.getsockname()[1]); s.close()")

    local container="gatesql-test-demo-$$"
    docker run -d --name "$container" \
        -p "${dashboard_port}:8080" \
        -p "${proxy_port}:15432" \
        "$DEMO_IMAGE" > /dev/null
    CONTAINERS+=("$container")

    # Wait for readiness
    if ! wait_for_api "$dashboard_port" "$api_key" 60; then
        fail "demo image failed to start within 60s"
        docker logs "$container"
        return 1
    fi
    pass "demo container started and API is ready"

    # Wait for entrypoint to create its MCP session (races with our readiness check)
    local sessions=""
    for i in $(seq 1 30); do
        sessions=$(curl -sf "http://localhost:${dashboard_port}/api/sessions" -H "X-Api-Key: ${api_key}")
        if echo "$sessions" | jq -e 'length > 0' > /dev/null 2>&1; then
            break
        fi
        sleep 1
    done
    if echo "$sessions" | jq -e 'length > 0' > /dev/null 2>&1; then
        pass "entrypoint created default MCP session"
    else
        fail "no default session found after startup"
    fi

    # Create a read-only session
    local response
    response=$(create_session "$dashboard_port" "$api_key" \
        '{"agentId":"smoke-test","task":"docker image test","queryBudget":50,"readOnly":true,"dangerousQueryMode":"block"}')
    local token session_id
    token=$(echo "$response" | jq -r '.token')
    session_id=$(echo "$response" | jq -r '.sessionId')
    if [ -n "$token" ] && [ "$token" != "null" ]; then
        pass "created read-only session ($session_id)"
    else
        fail "failed to create session"
        return 1
    fi

    # Query with purpose comment — should succeed
    local result
    if result=$(PGPASSWORD="$token" psql -h 127.0.0.1 -p "$proxy_port" -U agent -d demo \
        -c "/* <agent_purpose>smoke test</agent_purpose> */ SELECT count(*) FROM customers;" -t -A 2>&1); then
        if [ -n "$result" ] && [ "$result" -gt 0 ] 2>/dev/null; then
            pass "query with purpose comment returned data (${result} customers)"
        else
            fail "query returned unexpected result: $result"
        fi
    else
        fail "query with purpose comment failed: $result"
    fi

    # Query without purpose comment — should be rejected
    if PGPASSWORD="$token" psql -h 127.0.0.1 -p "$proxy_port" -U agent -d demo \
        -c "SELECT count(*) FROM customers;" -t -A > /dev/null 2>&1; then
        fail "query without purpose comment should have been rejected"
    else
        pass "query without purpose comment was rejected (governance enforced)"
    fi

    # Write query on read-only session — should be rejected
    if PGPASSWORD="$token" psql -h 127.0.0.1 -p "$proxy_port" -U agent -d demo \
        -c "/* <agent_purpose>smoke test</agent_purpose> */ INSERT INTO customers (name, email) VALUES ('test', 'test@test.com');" > /dev/null 2>&1; then
        fail "write query on read-only session should have been rejected"
    else
        pass "write query rejected on read-only session (read-only enforced)"
    fi

    # Verify session appears in list with queries used
    sessions=$(curl -sf "http://localhost:${dashboard_port}/api/sessions" -H "X-Api-Key: ${api_key}")
    if echo "$sessions" | jq -e ".[] | select(.sessionId == \"${session_id}\")" > /dev/null 2>&1; then
        pass "session visible in session list"
    else
        fail "session not found in session list"
    fi

    # Revoke session
    local revoke_status
    revoke_status=$(curl -sf -o /dev/null -w "%{http_code}" -X DELETE \
        "http://localhost:${dashboard_port}/api/sessions/${session_id}" \
        -H "X-Api-Key: ${api_key}")
    if [ "$revoke_status" = "200" ]; then
        pass "session revoked successfully"
    else
        fail "session revocation returned $revoke_status"
    fi

    # Schema endpoint
    local schema_status
    schema_status=$(curl -sf -o /dev/null -w "%{http_code}" \
        "http://localhost:${dashboard_port}/api/schema" \
        -H "X-Api-Key: ${api_key}")
    if [ "$schema_status" = "200" ]; then
        pass "schema endpoint returned 200"
    else
        fail "schema endpoint returned $schema_status"
    fi
}

# ─── Main Image ───────────────────────────────────────────────

test_main_image() {
    local api_key="pk_smoke_test"
    local dashboard_port proxy_port

    echo ""
    echo -e "${BOLD}Testing main image: ${MAIN_IMAGE}${NC}"
    echo ""

    dashboard_port=$(python3 -c "import socket; s=socket.socket(); s.bind(('',0)); print(s.getsockname()[1]); s.close()")
    proxy_port=$(python3 -c "import socket; s=socket.socket(); s.bind(('',0)); print(s.getsockname()[1]); s.close()")

    local network="gatesql-test-net-$$"
    docker network create "$network" > /dev/null
    NETWORKS+=("$network")

    # Start PostgreSQL
    local pg_container="gatesql-test-pg-$$"
    docker run -d --name "$pg_container" --network "$network" \
        -e POSTGRES_PASSWORD=testpass \
        -e POSTGRES_DB=testdb \
        postgres:17 > /dev/null
    CONTAINERS+=("$pg_container")

    # Wait for PG to be ready
    for i in $(seq 1 30); do
        if docker exec "$pg_container" pg_isready -q 2>/dev/null; then
            break
        fi
        if [ "$i" -eq 30 ]; then
            fail "PostgreSQL failed to start"
            return 1
        fi
        sleep 1
    done
    pass "PostgreSQL container ready"

    # Start GateSQL proxy
    local gatesql_container="gatesql-test-proxy-$$"
    docker run -d --name "$gatesql_container" --network "$network" \
        -p "${dashboard_port}:8080" \
        -p "${proxy_port}:15432" \
        -e GATESQL_UPSTREAM_HOST="$pg_container" \
        -e GATESQL_UPSTREAM_PORT=5432 \
        -e GATESQL_UPSTREAM_USER=postgres \
        -e GATESQL_UPSTREAM_PASSWORD=testpass \
        -e GATESQL_UPSTREAM_DATABASE=testdb \
        -e GATESQL_API_KEY="$api_key" \
        "$MAIN_IMAGE" > /dev/null
    CONTAINERS+=("$gatesql_container")

    if ! wait_for_api "$dashboard_port" "$api_key" 30; then
        fail "main image failed to start within 30s"
        docker logs "$gatesql_container"
        return 1
    fi
    pass "proxy container started and API is ready"

    # Create session
    local response
    response=$(create_session "$dashboard_port" "$api_key" \
        '{"agentId":"smoke-test","task":"main image test","queryBudget":50}')
    local token session_id
    token=$(echo "$response" | jq -r '.token')
    session_id=$(echo "$response" | jq -r '.sessionId')
    if [ -n "$token" ] && [ "$token" != "null" ]; then
        pass "created session ($session_id)"
    else
        fail "failed to create session"
        return 1
    fi

    # Wire protocol test
    local result
    if result=$(PGPASSWORD="$token" psql -h 127.0.0.1 -p "$proxy_port" -U agent -d testdb \
        -c "/* <agent_purpose>smoke test</agent_purpose> */ SELECT 1 AS ok;" -t -A 2>&1); then
        if [ "$result" = "1" ]; then
            pass "query through proxy returned correct result"
        else
            fail "query returned unexpected result: $result"
        fi
    else
        fail "query through proxy failed: $result"
    fi

    # Revoke session
    local revoke_status
    revoke_status=$(curl -sf -o /dev/null -w "%{http_code}" -X DELETE \
        "http://localhost:${dashboard_port}/api/sessions/${session_id}" \
        -H "X-Api-Key: ${api_key}")
    if [ "$revoke_status" = "200" ]; then
        pass "session revoked successfully"
    else
        fail "session revocation returned $revoke_status"
    fi
}

# ─── Run ──────────────────────────────────────────────────────

echo -e "${BOLD}GateSQL Docker Image Smoke Tests${NC}"
echo "================================="

test_demo_image
test_main_image

echo ""
echo "================================="
echo -e "${GREEN}Passed: ${PASSED}${NC}  ${RED}Failed: ${FAILED}${NC}"

if [ "$FAILED" -gt 0 ]; then
    exit 1
fi
