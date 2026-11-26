// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Mofumofu

package protocol

type TokenInfo struct {
	ID         string     `json:"id"`
	Name       string     `json:"name"`
	IsRoot     bool       `json:"isRoot"`
	Scopes     []string   `json:"scopes" protocolDefault:"empty"`
	CreatedAt  Timestamp  `json:"createdAt"`
	ExpiresAt  *Timestamp `json:"expiresAt"`
	LastUsedAt *Timestamp `json:"lastUsedAt"`
	RevokedAt  *Timestamp `json:"revokedAt"`
}

func (t *TokenInfo) UnmarshalJSON(data []byte) error {
	type alias TokenInfo
	var decoded alias
	if err := unmarshalInitialized(data, &decoded, false); err != nil {
		return err
	}
	*t = TokenInfo(decoded)
	return nil
}

type TokenListResponse struct {
	APIVersion *string     `json:"apiVersion" protocolDefault:"1.0"`
	Tokens     []TokenInfo `json:"tokens" protocolDefault:"empty"`
}

func (r TokenListResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type TokenDeriveRequest struct {
	APIVersion *string    `json:"apiVersion" protocolDefault:"1.0"`
	Name       string     `json:"name"`
	Scopes     []string   `json:"scopes" protocolDefault:"empty"`
	ExpiresAt  *Timestamp `json:"expiresAt"`
}

func (r TokenDeriveRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type TokenIssuedResponse struct {
	APIVersion *string   `json:"apiVersion" protocolDefault:"1.0"`
	Token      string    `json:"token"`
	TokenInfo  TokenInfo `json:"tokenInfo"`
}

func (r TokenIssuedResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type TokenRevokeRequest struct {
	APIVersion *string `json:"apiVersion" protocolDefault:"1.0"`
	TokenID    string  `json:"tokenId"`
}

func (r TokenRevokeRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type TokenRevokeResponse struct {
	APIVersion *string   `json:"apiVersion" protocolDefault:"1.0"`
	TokenID    string    `json:"tokenId"`
	RevokedAt  Timestamp `json:"revokedAt"`
}

func (r TokenRevokeResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type TokenRotateRequest struct {
	APIVersion         *string `json:"apiVersion" protocolDefault:"1.0"`
	TokenID            string  `json:"tokenId"`
	GracePeriodSeconds *int    `json:"gracePeriodSeconds" protocolDefault:"300"`
}

func (r TokenRotateRequest) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}

type TokenRotateResponse struct {
	APIVersion             *string   `json:"apiVersion" protocolDefault:"1.0"`
	Token                  string    `json:"token"`
	TokenInfo              TokenInfo `json:"tokenInfo"`
	PreviousTokenExpiresAt Timestamp `json:"previousTokenExpiresAt"`
}

func (r TokenRotateResponse) GetAPIVersion() string {
	return apiVersionValue(r.APIVersion)
}
