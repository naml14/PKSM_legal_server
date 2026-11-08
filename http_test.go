package main

import (
	"context"
	"testing"

	"github.com/FlagBrew/CoreAPI/internal/models"
	"github.com/apex/log"
)

// An empty or malformed entry in the trusted proxies list used to make the real
// IP middleware panic while the server was being built, so the API never came
// up. Those entries have to be ignored instead.
func TestHttpServerIgnoresUnusableTrustedProxies(t *testing.T) {
	originalFlags := cli.Flags
	originalLogger := logger
	t.Cleanup(func() {
		cli.Flags = originalFlags
		logger = originalLogger
	})

	// The flags are only filled in by cli.Parse(), which reads the command line.
	cli.Flags = &modelsFlags
	logger = log.Log

	server := httpServer(context.Background())
	if server == nil {
		t.Fatal("expected a server")
	}
}

var modelsFlags = models.Flags{
	HTTP: models.ConfigHTTP{
		TrustedProxies: []string{"", "   ", "not-an-ip", "127.0.0.1/32"},
	},
}
