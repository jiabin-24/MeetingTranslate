using MeetingTranscription.Models.Configuration;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace EchoBot.Translator
{
    /// <summary>
    /// 本身 Custom Speech 有 TranslationRecognizer，为什么还自己发起 HTTP 请求 - TranslationRecognizer 功能受限，不支持 category 动态路由、不支持多模型选择、不支持精细 batch 控制
    /// </summary>
    public sealed class AzureTranslatorClient : ITranslatorClient
    {
        private readonly HttpClient _http;
        private readonly string _endpoint;

        public AzureTranslatorClient(HttpClient http, IOptions<TranslatorOptions> options)
        {
            _http = http;

            var translatorOpt = options.Value;
            _endpoint = translatorOpt.Endpoint.TrimEnd('/');

            _http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", translatorOpt.Key);
            _http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Region", translatorOpt.Region);
        }

        public async Task<string> TranslateAsync(
            string text,
            string to,
            string category,
            CancellationToken ct = default)
        {
            var uri = !string.IsNullOrEmpty(category)
                ? $"{_endpoint}/translate?api-version=3.0&to={to}&category={category}"
                : $"{_endpoint}/translate?api-version=3.0&to={to}";
            var body = JsonSerializer.Serialize(new[] { new { Text = text } });

            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

            return doc.RootElement[0]
                .GetProperty("translations")[0]
                .GetProperty("text")
                .GetString()!;
        }

        public async Task<Dictionary<string, string>> BatchTranslateAsync(
            string text,
            Dictionary<string, string> toCategory,
            CancellationToken ct = default)
        {
            // Create a translation task for each route rule
            var tasks = toCategory.Keys.ToDictionary(to => to, to => TranslateAsync(text, to, toCategory[to], ct));

            // Await all translations concurrently
            var results = await Task.WhenAll(tasks.Values).ConfigureAwait(false);

            // Build result dictionary mapping target language -> translated text
            return tasks.ToDictionary(t => t.Key, t => t.Value.Result);
        }
    }
}
