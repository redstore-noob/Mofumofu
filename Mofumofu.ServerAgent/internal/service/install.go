// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package service

import (
	"context"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/audit"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/config"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/install"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/operations"
	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

const (
	defaultInstallPort       = 25565
	defaultInstallMaxPlayers = 20
	maxInstallMaxPlayers     = 1000
	defaultInstallMOTD       = "Mofumofu Server"
	maxInstallMOTDLength     = 256
)

// SetInstaller wires the install client. Installation still requires the
// config allowlist, attach mode off, and an InstallRootDirectory.
func (s *Service) SetInstaller(client *install.Client) { s.installer = client }

// PrepareInstall validates an install request against the allowlist and the
// target directory, then issues a single-use confirmation. Execute starts the
// install as an operation under the control scope.
func (s *Service) PrepareInstall(ctx context.Context, actor audit.Actor, request protocol.ServerInstallRequest) (protocol.ConfirmationPrepareResponse, error) {
	if s.installer == nil || s.confirmations == nil || s.operations == nil {
		return protocol.ConfirmationPrepareResponse{}, capabilityNotReady()
	}
	if s.config.Mode == config.ModeAttach || s.config.InstallRootDirectory == "" {
		return protocol.ConfirmationPrepareResponse{}, capabilityNotReady()
	}
	core, err := install.ParseCore(request.Core)
	if err != nil {
		return protocol.ConfirmationPrepareResponse{}, err
	}
	minecraftVersion := strings.TrimSpace(request.MinecraftVersion)
	if err := install.ValidateVersionID(minecraftVersion, "minecraftVersion"); err != nil {
		return protocol.ConfirmationPrepareResponse{}, err
	}
	allowlist := install.Allowlist{Cores: s.config.InstallAllowedCores, MinecraftVersions: s.config.InstallAllowedMinecraftVersions}
	if err := allowlist.Check(core, minecraftVersion); err != nil {
		return protocol.ConfirmationPrepareResponse{}, err
	}
	coreVersion := strings.TrimSpace(stringValue(request.CoreVersion))
	if coreVersion != "" {
		if core == install.CoreVanilla {
			return protocol.ConfirmationPrepareResponse{}, &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "vanilla does not accept coreVersion."}
		}
		if err := install.ValidateVersionID(coreVersion, "coreVersion"); err != nil {
			return protocol.ConfirmationPrepareResponse{}, err
		}
	}
	if !request.AcceptEula {
		return protocol.ConfirmationPrepareResponse{}, &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "The Minecraft EULA must be accepted."}
	}
	port := defaultInstallPort
	if request.Port != nil {
		port = *request.Port
		if port < 1 || port > 65535 {
			return protocol.ConfirmationPrepareResponse{}, &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "port must be between 1 and 65535."}
		}
	}
	maxPlayers := defaultInstallMaxPlayers
	if request.MaxPlayers != nil {
		maxPlayers = *request.MaxPlayers
		if maxPlayers < 1 || maxPlayers > maxInstallMaxPlayers {
			return protocol.ConfirmationPrepareResponse{}, &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "maxPlayers must be between 1 and 1000."}
		}
	}
	motd := defaultInstallMOTD
	if request.MOTD != nil {
		motd = *request.MOTD
		if utf16Length(motd) > maxInstallMOTDLength || hasControl(motd) {
			return protocol.ConfirmationPrepareResponse{}, &operations.Error{Status: 400, Code: protocol.CodeInvalidRequest, Message: "motd is invalid."}
		}
	}
	if err := install.CheckTarget(s.config.InstallRootDirectory, s.config.InstanceId); err != nil {
		return protocol.ConfirmationPrepareResponse{}, err
	}
	if s.audit == nil {
		return protocol.ConfirmationPrepareResponse{}, audit.ErrUnavailable
	}

	installRequest := install.Request{
		RootDirectory:    s.config.InstallRootDirectory,
		InstanceID:       s.config.InstanceId,
		Core:             core,
		MinecraftVersion: minecraftVersion,
		CoreVersion:      coreVersion,
		AcceptEULA:       true,
		Port:             port,
		MaxPlayers:       maxPlayers,
		MOTD:             motd,
		JavaPath:         s.config.InstallJavaPath,
	}
	parameters := installParameters(installRequest, motd)
	parameterMap := make(map[string]string, len(parameters))
	for _, parameter := range parameters {
		parameterMap[parameter.Key] = parameter.Value
	}
	summary := "Install " + string(core) + " " + minecraftVersion + " as " + s.config.InstanceId

	var response protocol.ConfirmationPrepareResponse
	err = s.audit.Execute(ctx, actor, "server.install.prepare", parameters, func() error {
		var err error
		response, err = s.confirmations.Prepare(s.config.InstanceId, actor.TokenID, protocol.ScopeControl,
			protocol.ConfirmationActionServerInstall, summary, "absent", parameterMap, func() (protocol.OperationInfo, error) {
				return s.operations.Start(context.Background(), "server.install", protocol.ScopeControl,
					s.installWork(actor, installRequest, motd))
			})
		return err
	})
	return response, err
}

func (s *Service) installWork(actor audit.Actor, request install.Request, motd string) operations.Work {
	return func(operationCtx context.Context) (operations.Completion, error) {
		if !s.installBusy.CompareAndSwap(false, true) {
			return operations.Completion{}, &operations.Error{Status: 409, Code: protocol.CodeConflict, Message: "Another server installation is already running."}
		}
		defer s.installBusy.Store(false)

		var result install.Result
		err := s.audit.Execute(operationCtx, actor, "server.install", installParameters(request, motd), func() error {
			var err error
			result, err = s.installer.Install(operationCtx, request, func(percent float64, message string) {
				operations.ReportProgress(operationCtx, percent, message)
			})
			return err
		})
		if err != nil {
			return operations.Completion{}, err
		}
		return operations.Completion{
			Message: "Server installed. Restart the Agent with ServerRootDirectory set to the install root to manage it.",
			Metadata: map[string]string{
				"instanceId":       s.config.InstanceId,
				"core":             string(request.Core),
				"minecraftVersion": result.MinecraftVersion,
				"coreVersion":      result.CoreVersion,
				"startScript":      filepath.Base(result.StartScriptPath),
			},
		}, nil
	}
}

// installParameters lists the audited install fields. MOTD text is reduced to its length.
func installParameters(request install.Request, motd string) audit.Parameters {
	return audit.Parameters{
		{Key: "core", Value: string(request.Core)},
		{Key: "minecraftVersion", Value: request.MinecraftVersion},
		{Key: "coreVersion", Value: request.CoreVersion},
		{Key: "port", Value: strconv.Itoa(request.Port)},
		{Key: "maxPlayers", Value: strconv.Itoa(request.MaxPlayers)},
		{Key: "motdLength", Value: strconv.Itoa(utf16Length(motd))},
		{Key: "acceptEula", Value: "true"},
	}
}
