// Copyright 2026 Mofumofu
// SPDX-License-Identifier: Apache-2.0

package operations

import (
	"context"
	"testing"

	"github.com/redstore-noob/Mofumofu/agent-go/internal/protocol"
)

func TestReportProgressPublishesRunningSnapshots(t *testing.T) {
	publisher := &recordingPublisher{}
	service := New(publisher, nil)
	t.Cleanup(func() { _ = service.Stop(context.Background()) })

	operation, err := service.Run(context.Background(), "server.install", protocol.ScopeControl,
		func(ctx context.Context) (Completion, error) {
			ReportProgress(ctx, 42, "Downloading server.jar")
			ReportProgress(context.Background(), 99, "outside an operation")
			return Completion{Message: "done"}, nil
		})
	if err != nil {
		t.Fatal(err)
	}
	if operation.State == nil || *operation.State != protocol.OperationStateSucceeded {
		t.Fatalf("terminal state = %+v", operation.State)
	}

	found := false
	for _, frame := range publisher.snapshot() {
		if frame.State != nil && *frame.State == protocol.OperationStateRunning &&
			frame.ProgressPercent != nil && *frame.ProgressPercent == 42 &&
			frame.Message != nil && *frame.Message == "Downloading server.jar" {
			found = true
		}
		if frame.ProgressPercent != nil && *frame.ProgressPercent == 99 {
			t.Fatal("ReportProgress outside an operation published a frame")
		}
	}
	if !found {
		t.Fatal("running progress snapshot was not published")
	}
}
