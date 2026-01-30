using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.RiquadroDebugTrasferimentiFirebase
{
    public sealed class TransferDebugHttpHandler : DelegatingHandler
    {
        private readonly string _clientLabel;
        private readonly FirebaseTransferDebugMonitor _monitor;

        public TransferDebugHttpHandler(string clientLabel, FirebaseTransferDebugMonitor monitor)
            : base(new HttpClientHandler())
        {
            _clientLabel = clientLabel ?? "API";
            _monitor = monitor ?? FirebaseTransferDebugMonitor.Instance;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.Method.Method.ToUpperInvariant();
            var direction = method == HttpMethod.Get.Method.ToUpperInvariant() ? TransferDirection.Down : TransferDirection.Up;
            var endpoint = BuildEndpointLabel(_clientLabel, request.RequestUri, method);
            long? requestBytes = request.Content?.Headers.ContentLength;

            var token = _monitor.BeginApi(direction, method, endpoint, requestBytes);
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                long? responseBytes = response.Content?.Headers.ContentLength;
                _monitor.EndApi(token, TransferOutcome.Success, (int)response.StatusCode, responseBytes, null);
                return response;
            }
            catch (TaskCanceledException ex)
            {
                var outcome = cancellationToken.IsCancellationRequested
                    ? TransferOutcome.Cancelled
                    : TransferOutcome.Timeout;

                _monitor.EndApi(token, outcome, response != null ? (int)response.StatusCode : null, null, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _monitor.EndApi(token, TransferOutcome.Fail, response != null ? (int)response.StatusCode : null, null, ex.Message);
                throw;
            }
        }

        private static string BuildEndpointLabel(string clientLabel, Uri? uri, string method)
        {
            if (uri == null)
                return string.Format(CultureInfo.InvariantCulture, "{0} {1} (null)", clientLabel, method);

            var host = uri.Host;
            var path = uri.AbsolutePath;
            var query = MaskQuery(uri.Query);
            var suffix = string.IsNullOrWhiteSpace(query) ? path : string.Concat(path, "?", query);
            return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}{3}", clientLabel, method, host, suffix);
        }

        private static string MaskQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return "";
            var trimmed = query.TrimStart('?');
            var parts = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var kvp = parts[i].Split('=', 2);
                if (kvp.Length == 0) continue;
                var key = kvp[0];
                var value = kvp.Length > 1 ? kvp[1] : "";
                if (IsSensitiveKey(key))
                    value = "***";

                parts[i] = kvp.Length > 1 ? $"{key}={value}" : key;
            }

            return string.Join("&", parts);
        }

        private static bool IsSensitiveKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            var lower = key.ToLowerInvariant();
            return lower.Contains("key") || lower.Contains("token") || lower.Contains("auth");
        }
    }
}
