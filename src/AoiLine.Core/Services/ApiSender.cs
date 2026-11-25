using System.Text;
using AoiLine.Core.Models;

namespace AoiLine.Core.Services;

public class ApiResult
{
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string Error { get; set; } = "";
    public int Attempts { get; set; }
}

public static class ApiSender
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<ApiResult> SendAsync(ApiSettings api, string body, CancellationToken ct)
    {
        var result = new ApiResult();
        int attempts = Math.Max(1, api.RetryCount + 1);

        for (int a = 1; a <= attempts; a++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, api.TimeoutSeconds)));

                var method = new HttpMethod(string.IsNullOrWhiteSpace(api.Method) ? "POST" : api.Method.Trim().ToUpperInvariant());
                using var req = new HttpRequestMessage(method, api.Url.Trim());
                if (method != HttpMethod.Get)
                {
                    var contentType = string.IsNullOrWhiteSpace(api.ContentType) ? "application/json" : api.ContentType.Trim();
                    req.Content = new StringContent(body, Encoding.UTF8, contentType);
                }
                foreach (var h in api.Headers.Where(h => !string.IsNullOrWhiteSpace(h.Name)))
                {
                    if (!req.Headers.TryAddWithoutValidation(h.Name.Trim(), h.Value))
                        req.Content?.Headers.TryAddWithoutValidation(h.Name.Trim(), h.Value);
                }

                using var resp = await Client.SendAsync(req, cts.Token);
                var text = await resp.Content.ReadAsStringAsync(cts.Token);

                result = new ApiResult { StatusCode = (int)resp.StatusCode, ResponseBody = text, Attempts = a };
                bool containsOk = string.IsNullOrEmpty(api.SuccessContains) ||
                                  text.Contains(api.SuccessContains, StringComparison.OrdinalIgnoreCase);
                if (resp.IsSuccessStatusCode && containsOk)
                {
                    result.Success = true;
                    return result;
                }
                result.Error = resp.IsSuccessStatusCode
                    ? $"Response does not contain '{api.SuccessContains}'"
                    : $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = new ApiResult { Error = $"Timeout after {api.TimeoutSeconds}s", Attempts = a };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new ApiResult { Error = ex.InnerException?.Message ?? ex.Message, Attempts = a };
            }

            if (a < attempts) await Task.Delay(2000, ct);
        }
        return result;
    }
}
