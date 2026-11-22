using System.Net.Http;

using MofuCore.Server;

namespace MofuCore.Config;

// 复用 ServerNetworkGuard 的 HTTPS 强制与 DNS 私网拦截，对重定向逐跳校验，避免 302 把请求引向内网或云元数据地址。
internal static class DownloadUrlGuard
{
    private const int MaxRedirects = 5;

    /// <summary>逐跳校验后返回最终响应（调用方负责 Dispose 与 EnsureSuccessStatusCode）；client 应设置 AllowAutoRedirect=false。</summary>
    internal static async Task<HttpResponseMessage> SendWithRedirectGuardAsync(
        HttpClient client, string url, CancellationToken cancellationToken)
    {
        Uri current = await ServerNetworkGuard.ValidatePublicHttpUrlAsync(url, cancellationToken).ConfigureAwait(false);
        for (int hop = 0; ; hop++)
        {
            if (hop > MaxRedirects)
            {
                throw new InvalidOperationException("下载地址重定向次数过多");
            }

            HttpResponseMessage response = await client
                .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status >= 300 && status < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                current = await ServerNetworkGuard
                    .ValidatePublicHttpUrlAsync(new Uri(current, location).ToString(), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            // 兜底：client 若仍允许自动重定向，最终 RequestUri 可能已被换到别的主机，再校验一次
            Uri? final = response.RequestMessage?.RequestUri;
            if (final is not null && !string.Equals(final.ToString(), current.ToString(), StringComparison.Ordinal))
            {
                try
                {
                    await ServerNetworkGuard.ValidatePublicHttpUrlAsync(final.ToString(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            return response;
        }
    }
}
