// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

type BackupMetadata struct {
	ID               string    `json:"id"`
	Label            string    `json:"label"`
	Description      *string   `json:"description"`
	CreatedAt        Timestamp `json:"createdAt"`
	SizeBytes        int64     `json:"sizeBytes"`
	SHA256           string    `json:"sha256"`
	MinecraftVersion *string   `json:"minecraftVersion"`
}

type BackupListResponse struct {
	APIVersion *string          `json:"apiVersion" protocolDefault:"1.0"`
	Backups    []BackupMetadata `json:"backups" protocolDefault:"empty"`
}

func (r BackupListResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type BackupCreateRequest struct {
	APIVersion  *string `json:"apiVersion" protocolDefault:"1.0"`
	Label       string  `json:"label"`
	Description *string `json:"description"`
}

func (r BackupCreateRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type BackupCreateResponse struct {
	APIVersion *string        `json:"apiVersion" protocolDefault:"1.0"`
	Backup     BackupMetadata `json:"backup"`
	Operation  OperationInfo  `json:"operation"`
}

func (r BackupCreateResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type BackupRestoreRequest struct {
	APIVersion *string `json:"apiVersion" protocolDefault:"1.0"`
	BackupID   string  `json:"backupId"`
}

func (r BackupRestoreRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

func (BackupRestoreRequest) confirmationPrepareRequest() {}

type BackupDeleteRequest struct {
	APIVersion *string `json:"apiVersion" protocolDefault:"1.0"`
	BackupID   string  `json:"backupId"`
}

func (r BackupDeleteRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

func (BackupDeleteRequest) confirmationPrepareRequest() {}
