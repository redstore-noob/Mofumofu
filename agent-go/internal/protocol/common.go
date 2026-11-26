// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

// IProtocolMessage is implemented by every versioned protocol DTO.
type IProtocolMessage interface {
	GetAPIVersion() string
}

type APIError struct {
	APIVersion  *string           `json:"apiVersion" protocolDefault:"1.0"`
	Code        string            `json:"code"`
	Message     string            `json:"message"`
	Details     map[string]string `json:"details"`
	RequestID   *string           `json:"requestId"`
	OperationID *string           `json:"operationId"`
}

// ApiError is an alias matching the C# spelling.
type ApiError = APIError

func (e APIError) GetAPIVersion() string {
	return apiVersionValue(e.APIVersion)
}

func (e *APIError) UnmarshalJSON(data []byte) error {
	type alias APIError
	var decoded alias
	if err := unmarshalInitialized(data, &decoded, true); err != nil {
		return err
	}
	*e = APIError(decoded)
	return nil
}

type OperationInfo struct {
	ID              string            `json:"id"`
	Kind            string            `json:"kind"`
	State           *string           `json:"state" protocolDefault:"queued"`
	CreatedAt       Timestamp         `json:"createdAt"`
	StartedAt       *Timestamp        `json:"startedAt"`
	CompletedAt     *Timestamp        `json:"completedAt"`
	ProgressPercent *float64          `json:"progressPercent"`
	Message         *string           `json:"message"`
	Metadata        map[string]string `json:"metadata"`
	Error           *APIError         `json:"error"`
}

func (o *OperationInfo) UnmarshalJSON(data []byte) error {
	type alias OperationInfo
	var decoded alias
	if err := unmarshalInitialized(data, &decoded, false); err != nil {
		return err
	}
	*o = OperationInfo(decoded)
	return nil
}

type OperationResponse struct {
	APIVersion *string       `json:"apiVersion" protocolDefault:"1.0"`
	Operation  OperationInfo `json:"operation"`
}

func (r OperationResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type ConfirmationPrepareRequest interface {
	IProtocolMessage
	confirmationPrepareRequest()
}

type ConfirmationPrepareResponse struct {
	APIVersion       *string   `json:"apiVersion" protocolDefault:"1.0"`
	ConfirmationID   string    `json:"confirmationId"`
	Action           string    `json:"action"`
	Summary          string    `json:"summary"`
	CreatedAt        Timestamp `json:"createdAt"`
	ExpiresAt        Timestamp `json:"expiresAt"`
	ExpiresInSeconds *int      `json:"expiresInSeconds" protocolDefault:"60"`
}

func (r ConfirmationPrepareResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type ConfirmationExecuteRequest struct {
	APIVersion     *string `json:"apiVersion" protocolDefault:"1.0"`
	ConfirmationID string  `json:"confirmationId"`
}

func (r ConfirmationExecuteRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type ConfirmationExecuteResponse struct {
	APIVersion *string       `json:"apiVersion" protocolDefault:"1.0"`
	Operation  OperationInfo `json:"operation"`
}

func (r ConfirmationExecuteResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type ServerEvent struct {
	EventID    string            `json:"eventId"`
	Type       string            `json:"type"`
	OccurredAt Timestamp         `json:"occurredAt"`
	Message    string            `json:"message"`
	Details    map[string]string `json:"details" protocolDefault:"empty"`
}

func (e *ServerEvent) UnmarshalJSON(data []byte) error {
	type alias ServerEvent
	var decoded alias
	if err := unmarshalInitialized(data, &decoded, false); err != nil {
		return err
	}
	*e = ServerEvent(decoded)
	return nil
}
