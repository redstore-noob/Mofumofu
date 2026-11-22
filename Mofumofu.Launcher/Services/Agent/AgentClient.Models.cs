using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mofumofu.Launcher.Services.Agent;

public static partial class AgentClient
{
    private const string AnthropicVersion = "2023-06-01";

    public static async Task<(bool Ok, string Message, List<string> Models)> ListModelsAsync(
        AgentProviderConfig provider, CancellationToken cancellationToken)
    {
        string baseUrl = AgentProviderPresets.NormalizeBaseUrl(provider.BaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return (false, "先填 Base URL。", new List<string>());
        if (AgentSecretProtector.LooksEncrypted(provider.ApiKey))
            return (false, "API Key 无法解密，请重新填写后再拉取模型。", new List<string>());

        try
        {
            using HttpRequestMessage http = BuildRequest(provider, baseUrl, out string url);
            using HttpResponseMessage response = await Http.SendAsync(http, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string error = await ReadErrorAsync(response, cancellationToken);
                return (false, "拉取失败：" + error, new List<string>());
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            List<string> models = ParseModels(provider.Kind, json);
            if (models.Count == 0)
                return (false, "接口返回里没有可用的模型 id。", new List<string>());

            return (true, $"拉到 {models.Count} 个模型（{Shorten(url)}）。", models);
        }
        catch (OperationCanceledException)
        {
            return (false, "拉取已取消或超时。", new List<string>());
        }
        catch (Exception error)
        {
            return (false, "拉取失败：" + error.Message, new List<string>());
        }
    }

    private static HttpRequestMessage BuildRequest(
        AgentProviderConfig provider, string baseUrl, out string url)
    {
        switch (provider.Kind)
        {
            case AgentProviderKind.Anthropic:
                url = AgentClient.TrimVersionedBase(baseUrl) + "/v1/models?limit=100";
                HttpRequestMessage anthropic = Build(HttpMethod.Get, url, provider);
                if (AgentProviderPresets.IsOfficialEndpoint(provider.BaseUrl) &&
                    !string.IsNullOrWhiteSpace(provider.ApiKey))
                    anthropic.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey.Trim());
                anthropic.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                return anthropic;

            case AgentProviderKind.Gemini:
            {
                url = baseUrl + "/models?pageSize=200";
                HttpRequestMessage gemini = Build(HttpMethod.Get, url, provider);
                if (AgentProviderPresets.IsOfficialEndpoint(provider.BaseUrl) &&
                    !string.IsNullOrWhiteSpace(provider.ApiKey))
                    gemini.Headers.TryAddWithoutValidation("x-goog-api-key", provider.ApiKey.Trim());
                return gemini;
            }

            default:
                url = baseUrl + "/models";
                HttpRequestMessage openAi = Build(HttpMethod.Get, url, provider);
                if (AgentProviderPresets.IsOfficialEndpoint(provider.BaseUrl) &&
                    !string.IsNullOrWhiteSpace(provider.ApiKey))
                {
                    openAi.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey.Trim());
                }

                return openAi;
        }
    }

    private static List<string> ParseModels(AgentProviderKind kind, string json)
    {
        var models = new List<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            JsonElement list = root;
            if (kind == AgentProviderKind.Gemini && root.TryGetProperty("models", out JsonElement gemini))
                list = gemini;
            else if (root.TryGetProperty("data", out JsonElement data))
                list = data;
            else if (root.TryGetProperty("models", out JsonElement fallback))
                list = fallback;

            if (list.ValueKind != JsonValueKind.Array) return models;

            foreach (JsonElement item in list.EnumerateArray())
            {
                string? id = null;
                if (item.TryGetProperty("id", out JsonElement idNode))
                    id = idNode.GetString();
                else if (item.TryGetProperty("name", out JsonElement nameNode))
                    id = nameNode.GetString();

                if (string.IsNullOrWhiteSpace(id)) continue;
                id = id.Trim();
                if (id.StartsWith("models/", StringComparison.Ordinal))
                    id = id["models/".Length..];

                // Gemini 会返回只支持 embedding 的模型，过滤掉，避免选了没法聊天
                if (kind == AgentProviderKind.Gemini &&
                    item.TryGetProperty("supportedGenerationMethods", out JsonElement methods) &&
                    methods.ValueKind == JsonValueKind.Array &&
                    !methods.EnumerateArray().Any(m =>
                        string.Equals(m.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!models.Contains(id, StringComparer.OrdinalIgnoreCase)) models.Add(id);
            }
        }
        catch (JsonException)
        {
            return models;
        }

        models.Sort(StringComparer.OrdinalIgnoreCase);
        return models.Take(500).ToList();
    }

    private static string Shorten(string url)
    {
        int query = url.IndexOfAny(new[] { '?', '#' });
        if (query >= 0) url = url[..query];
        return url.Length <= 64 ? url : url[..61] + "…";
    }
}
