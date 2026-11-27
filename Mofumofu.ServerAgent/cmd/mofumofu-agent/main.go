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
	"github.com/redstore-noob/Mofumofu/agent-go/internal/backups"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/confirmation"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/events"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/files"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/httpapi"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/install"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/operations"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/players"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/probe"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/server"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/service"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tokens"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/tui"
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
	tuiRequested, tuiDisabled := false, false
	for _, argument := range args {
		switch strings.ToLower(argument) {
		case "--tui":
			tuiRequested = true
		case "--no-tui":
			tuiDisabled = true
		}
	}
	if tuiRequested && tuiDisabled {
		return fmt.Errorf("--tui and --no-tui cannot be used together")
	}
	if err := validateArguments(args); err != nil {
		return err
	}
	cfg, err := config.Load("", environ, args)
	if err != nil {
		return err
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

	journal, err := events.OpenJournal(cfg.EventJournalPath, cfg.EventJournalCapacity, time.Now, func(err error) {
		logger.Error("could not append console to event journal", "error", err)
	})
	if err != nil {
		return fmt.Errorf("open event journal: %w", err)
	}
	defer func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = journal.Close(ctx)
	}()
	onConsole := func(line string) {
		if err := journal.EnqueueConsole(line); err != nil {
			logger.Error("could not enqueue console line", "error", err)
		}
	}
	var supervisor service.Supervisor
	var attach *server.AttachSupervisor
	if cfg.Mode == config.ModeAttach {
		attach = server.NewAttachSupervisor(metadata, logger, time.Now, onConsole)
		supervisor = attach
	} else {
		supervisor = server.NewSupervisor(metadata, server.Options{Logger: logger, Environ: environ, OnConsole: onConsole})
	}
	defer func() {
		ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
		defer cancel()
		supervisor.Shutdown(ctx, 20*time.Second)
	}()
	operationService := operations.New(journal, time.Now)
	confirmationService := confirmation.New(time.Now)
	registry := tokens.NewTokenRegistry(cfg.ProbeRegistryPath, time.Now)
	limiter := tokens.NewRateLimiter(time.Now)
	application := service.New(cfg, metadata, version, time.Now)
	application.SetSupervisor(supervisor)
	application.SetAudit(auditSink)
	application.SetOperations(operationService, confirmationService)
	application.SetJournal(journal)
	application.SetRegistry(registry)
	application.SetEventPublisher(func(event protocol.ServerEvent) {
		if err := journal.AppendEvent(context.Background(), event); err != nil {
			logger.Error("could not persist Agent event", "error", err)
		}
	}, true)
	fileService, err := files.New(metadata.Directory, cfg.ProbeRegistryPath, cfg.AuditPath, cfg.EventJournalPath)
	if err != nil {
		return fmt.Errorf("initialize safe file service: %w", err)
	}
	application.SetFiles(fileService, files.NewPropertyService(fileService, time.Now))
	application.SetBackups(backups.New(metadata.Directory, cfg.ServerRootDirectory, metadata.Config.MCVersion, supervisor, time.Now, logger))
	application.SetInstaller(install.NewClient(install.Options{UserAgent: "mofumofu-agent/" + version}))
	var probeClient *probe.Client
	if cfg.ProbeEnabled {
		probeClient, err = probe.New(cfg.ProbeBaseUrl)
		if err != nil {
			return fmt.Errorf("initialize Probe gateway: %w", err)
		}
	}
	application.SetPlayers(players.New(metadata.Directory, supervisor, time.Now), probeClient)
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
	if attach != nil {
		go attach.Run(ctx)
	}
	logger.Info("Agent started", "version", version, "instanceId", cfg.InstanceId, "bind", listener.Addr().String(), "capabilities", application.Capabilities())
	if !tuiDisabled && (tuiRequested || tui.IsTTY(os.Stdin, os.Stdout)) {
		if tui.IsTTY(os.Stdin, os.Stdout) {
			go func() {
				if err := tui.Run(ctx, tui.WrapService(application), version); err != nil {
					if ctx.Err() != nil {
						return
					}
					logger.Error("TUI stopped; Agent remains running", "error", err)
					return
				}
				stop()
			}()
		} else {
			logger.Info("TUI was requested but stdin/stdout is not a terminal; running headless")
		}
	}
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
	operationCtx, operationCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer operationCancel()
	if err := operationService.Stop(operationCtx); err != nil {
		logger.Warn("operation shutdown did not complete", "error", err)
	}
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
		lower := strings.ToLower(argument)
		if lower == "--config" || lower == "--urls" {
			if index+1 >= len(args) || strings.HasPrefix(args[index+1], "--") {
				return fmt.Errorf("%s requires a value", argument)
			}
			index++
			continue
		}
		if strings.HasPrefix(lower, "--config=") || strings.HasPrefix(lower, "--urls=") {
			continue
		}
		if lower == "--version" || lower == "-version" || lower == "--help" || lower == "-h" || lower == "--tui" || lower == "--no-tui" {
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
  --tui             Enable the local terminal UI when stdin/stdout are TTYs
  --no-tui          Disable the terminal UI
  --version         Print the build version
  --help            Show this help`)
}
