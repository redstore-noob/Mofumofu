// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

type FileEntry struct {
	Path        string     `json:"path"`
	IsDirectory bool       `json:"isDirectory"`
	SizeBytes   *int64     `json:"sizeBytes"`
	ModifiedAt  *Timestamp `json:"modifiedAt"`
	Revision    *string    `json:"revision"`
	Writable    bool       `json:"writable"`
}

type FileListResponse struct {
	APIVersion *string     `json:"apiVersion" protocolDefault:"1.0"`
	Path       string      `json:"path"`
	Entries    []FileEntry `json:"entries" protocolDefault:"empty"`
}

func (r FileListResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type FileContentResponse struct {
	APIVersion *string    `json:"apiVersion" protocolDefault:"1.0"`
	Path       string     `json:"path"`
	Content    string     `json:"content"`
	Encoding   *string    `json:"encoding" protocolDefault:"utf-8"`
	Revision   string     `json:"revision"`
	SizeBytes  int64      `json:"sizeBytes"`
	ModifiedAt *Timestamp `json:"modifiedAt"`
}

func (r FileContentResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type FileWriteRequest struct {
	APIVersion       *string `json:"apiVersion" protocolDefault:"1.0"`
	Path             string  `json:"path"`
	Content          string  `json:"content"`
	ExpectedRevision string  `json:"expectedRevision"`
}

func (r FileWriteRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type FileWriteResponse struct {
	APIVersion *string   `json:"apiVersion" protocolDefault:"1.0"`
	Path       string    `json:"path"`
	Revision   string    `json:"revision"`
	SizeBytes  int64     `json:"sizeBytes"`
	ModifiedAt Timestamp `json:"modifiedAt"`
}

func (r FileWriteResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type FileCreateRequest struct {
	APIVersion  *string `json:"apiVersion" protocolDefault:"1.0"`
	Path        string  `json:"path"`
	IsDirectory bool    `json:"isDirectory"`
}

func (r FileCreateRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type FileRenameRequest struct {
	APIVersion       *string `json:"apiVersion" protocolDefault:"1.0"`
	Path             string  `json:"path"`
	NewName          string  `json:"newName"`
	ExpectedRevision string  `json:"expectedRevision"`
}

func (r FileRenameRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type FileMutationResponse struct {
	APIVersion  *string   `json:"apiVersion" protocolDefault:"1.0"`
	Path        string    `json:"path"`
	Revision    string    `json:"revision"`
	IsDirectory bool      `json:"isDirectory"`
	ModifiedAt  Timestamp `json:"modifiedAt"`
}

func (r FileMutationResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type FileDeleteRequest struct {
	APIVersion       *string `json:"apiVersion" protocolDefault:"1.0"`
	Path             string  `json:"path"`
	ExpectedRevision string  `json:"expectedRevision"`
}

func (r FileDeleteRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

func (FileDeleteRequest) confirmationPrepareRequest() {}
