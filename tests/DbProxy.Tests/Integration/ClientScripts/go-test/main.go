package main

import (
	"context"
	"fmt"
	"os"
	"strings"

	"github.com/jackc/pgx/v5"
)

func main() {
	host := os.Getenv("PGHOST")
	port := os.Getenv("PGPORT")
	password := os.Getenv("PGPASSWORD")

	connStr := fmt.Sprintf("host=%s port=%s user=agent password=%s dbname=postgres sslmode=disable", host, port, password)
	ctx := context.Background()

	conn, err := pgx.Connect(ctx, connStr)
	if err != nil {
		fmt.Fprintf(os.Stderr, "connect failed: %v\n", err)
		os.Exit(1)
	}
	defer conn.Close(ctx)

	// Query with purpose
	fmt.Println("=== pgx: query with purpose ===")
	var result int
	err = conn.QueryRow(ctx, "/* <agent_purpose>go e2e test</agent_purpose> */ SELECT 42").Scan(&result)
	if err != nil {
		fmt.Fprintf(os.Stderr, "FAIL: %v\n", err)
		os.Exit(1)
	}
	if result != 42 {
		fmt.Fprintf(os.Stderr, "FAIL: expected 42, got %d\n", result)
		os.Exit(1)
	}
	fmt.Printf("OK: got %d\n", result)

	// Query without purpose (should fail)
	fmt.Println("=== pgx: query without purpose (should fail) ===")
	err = conn.QueryRow(ctx, "SELECT 1").Scan(&result)
	if err == nil {
		fmt.Fprintln(os.Stderr, "FAIL: query without purpose was not rejected")
		os.Exit(1)
	}
	if strings.Contains(err.Error(), "agent_purpose") {
		fmt.Println("OK: rejected without purpose")
	} else {
		fmt.Fprintf(os.Stderr, "FAIL: unexpected error: %v\n", err)
		os.Exit(1)
	}

	fmt.Println("=== pgx: all tests passed ===")
}
