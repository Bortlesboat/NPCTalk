using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NpcVoice
{
    internal sealed class ChatMessage
    {
        public string Role;
        public string Content;
        public ChatMessage(string role, string content) { Role = role; Content = content; }
    }

    /// <summary>OpenAI-style chat completions; defaults to Cerebras for sub-second dialogue.</summary>
    internal sealed class LlmClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string _url, _apiKey, _model, _reasoningEffort;
        private readonly bool _disableReasoning;
        private readonly int _maxTokens;
        private readonly double _temperature;

        public LlmClient(string baseUrl, string apiKey, string model, int maxTokens, double temperature, string reasoningEffort, bool disableReasoning)
        {
            _url = baseUrl.TrimEnd('/') + "/chat/completions";
            _apiKey = apiKey;
            _model = model;
            _maxTokens = maxTokens;
            _temperature = temperature;
            _reasoningEffort = reasoningEffort;
            _disableReasoning = disableReasoning;
        }

        public bool IsConfigured => !string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(_model);

        public string Model => _model;

        public string Url => _url;

        /// <summary>
        /// Cerebras usually answers in ~0.3 s but occasionally queues for 5+ s; a fresh request
        /// after 2.5 s usually lands on a free slot, so retry once instead of waiting it out.
        /// When it's busy it answers 429 "queue_exceeded" straight away; those are retried after a
        /// short pause too.
        /// </summary>
        public async Task<string> CompleteAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
        {
            var list = messages.ToList();
            int[] busyPausesMs = { 300, 800, 1500 };
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fast = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        fast.CancelAfter(TimeSpan.FromSeconds(2.5));
                        try { return await CompleteOnceAsync(list, fast.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    }
                    return await CompleteOnceAsync(list, ct).ConfigureAwait(false);
                }
                catch (LlmBusyException) when (attempt < busyPausesMs.Length)
                {
                    await Task.Delay(busyPausesMs[attempt], ct).ConfigureAwait(false);
                }
            }
        }

        private sealed class LlmBusyException : IOException
        {
            public LlmBusyException(string message) : base(message) { }
        }

        private async Task<string> CompleteOnceAsync(List<ChatMessage> messages, CancellationToken ct)
        {
            var msgs = new List<Dictionary<string, string>>();
            foreach (var m in messages) msgs.Add(new Dictionary<string, string> { ["role"] = m.Role, ["content"] = m.Content });

            var body = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["messages"] = msgs,
                ["max_completion_tokens"] = _maxTokens,
                ["temperature"] = _temperature,
            };
            if (!string.IsNullOrEmpty(_reasoningEffort)) body["reasoning_effort"] = _reasoningEffort;
            if (_disableReasoning) body["disable_reasoning"] = true; // Cerebras: answer straight away, no hidden thinking

            var serializer = new JavaScriptSerializer();
            var req = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = new StringContent(serializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);

            using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
            {
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string error = $"LLM {(int)resp.StatusCode}: {(json.Length > 200 ? json.Substring(0, 200) : json)}";
                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode == 503) throw new LlmBusyException(error);
                    throw new IOException(error);
                }

                var result = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (result == null || !result.TryGetValue("choices", out var choices) || !(choices is object[] list) || list.Length == 0)
                    throw new IOException("LLM returned no choices");
                var message = (Dictionary<string, object>)((Dictionary<string, object>)list[0])["message"];
                // Reasoning models that run out of tokens return only "reasoning" and no "content".
                return message.TryGetValue("content", out var content) ? ((content as string) ?? "").Trim() : "";
            }
        }
    }
}
