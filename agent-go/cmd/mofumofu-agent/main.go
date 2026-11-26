// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/httpapi"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
)

var version = "dev"

func main() {
	if err := run(os.Args[1:], os.Environ()); err != nil {
		fmt.Fprintln(os.Stderr, "mofumofu-agent:", err)
		os.Exit(1)
	}
}

func run(args, environ []string) error {
	for _, argument := range args {
		switch argument {
		case "--version", "-version":
			fmt.Println(version)
			return nil
		case "--help", "-h":
			printHelp()
			return nil
		}
	}
	if err := validateArguments(args); err != nil {
		return err
	}
	cfg, err := config.Load("", environ, args)
	if err != nil {
		return err
	}
	if cfg.Mode != config.ModeManaged {
		return fmt.Errorf("attach mode is not implemented yet; use managed mode")
	}
	logger := slog.New(slog.NewJSONHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(logger)
	metadata, err := server.LoadMetadata(cfg.ServerRootDirectory, cfg.InstanceId)
	if err != nil {
		return fmt.Errorf("existing instance %q was not found or server.json is invalid: %w", cfg.InstanceId, err)
	}
	if err := os.MkdirAll(filepath.Dir(cfg.AuditPath), 0o750); err != nil {
		return fmt.Errorf("create audit directory: %w", err)
	}
	auditSink, err := audit.Open(cfg.AuditPath)
	if err != nil {
		return fmt.Errorf("open audit journal: %w", err)
	}
	defer auditSink.Close()

	supervisor := server.NewSupervisor(metadata, server.Options{Logger: logger, Environ: os.Environ()})
	defer supervisor.Close()
	registry := tokens.NewTokenRegistry(cfg.ProbeRegistryPath, time.Now)
	limiter := tokens.NewRateLimiter(time.Now)
	application := service.New(cfg, metadata, version, time.Now)
	application.SetSupervisor(supervisor)
	application.SetAudit(auditSink)
	security := &httpapi.Security{InstanceID: cfg.InstanceId, Registry: registry, Limiter: limiter}
	api := httpapi.New(application, security, logger)

	port, err := cfg.BindPort()
	if err != nil {
		return err
	}
	listener, err := net.Listen("tcp4", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		return fmt.Errorf("listen on loopback: %w", err)
	}
	httpServer := &http.Server{
		Handler:           api.Handler(),
		ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout:       2 * time.Minute,
		MaxHeaderBytes:    1 << 20,
		ErrorLog:          slog.NewLogLogger(logger.Handler(), slog.LevelError),
	}
	serveErrors := make(chan error, 1)
	go func() { serveErrors <- httpServer.Serve(listener) }()

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	application.StartMonitor(ctx)
	logger.Info("Agent started", "version", version, "instanceId", cfg.InstanceId, "bind", listener.Addr().String(), "capabilities", application.Capabilities())
	select {
	case <-ctx.Done():
		logger.Info("Agent stopping")
	case err := <-serveErrors:
		if err != nil && !errors.Is(err, http.ErrServerClosed) {
			return err
		}
		return nil
	}

	processCtx, processCancel := context.WithTimeout(context.Background(), 20*time.Second)
	application.Shutdown(processCtx, 20*time.Second)
	processCancel()
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := httpServer.Shutdown(shutdownCtx); err != nil {
		_ = httpServer.Close()
		return fmt.Errorf("HTTP shutdown: %w", err)
	}
	logger.Info("Agent stopped")
	return nil
}

func validateArguments(args []string) error {
	for index := 0; index < len(args); index++ {
		argument := args[index]
		if argument == "--config" || argument == "--urls" {
			if index+1 >= len(args) || strings.HasPrefix(args[index+1], "--") {
				return fmt.Errorf("%s requires a value", argument)
			}
			index++
			continue
		}
		if strings.HasPrefix(argument, "--config=") || strings.HasPrefix(argument, "--urls=") {
			continue
		}
		if argument == "--version" || argument == "-version" || argument == "--help" || argument == "-h" {
			continue
		}
		return fmt.Errorf("unknown option %q", argument)
	}
	return nil
}

func printHelp() {
	fmt.Println(`mofumofu-agent - Mofumofu VPS ServerAgent

Usage:
  mofumofu-agent --config <path> [--urls http://127.0.0.1:17870]

Options:
  --config <path>   ServerAgent JSON configuration (required)
  --urls <url>      Override the IPv4 loopback listener URL
  --version         Print the build version
  --help            Show this help`)
}
