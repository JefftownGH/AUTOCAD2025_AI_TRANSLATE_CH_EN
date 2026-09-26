using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AutoCAD.AITranslate
{
    /// <summary>
    /// Thrown when the server responds with 404, i.e. the endpoint does not exist
    /// on the configured base URL. Used to drive the Responses -> ChatCompletions fallback.
    /// </summary>
    internal sealed class ApiNotFoundException : Exception
    {
        public ApiNotFoundException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Thrown when the API responded successfully but the payload could not be parsed
    /// or contained no usable text. Carries a truncated copy of the raw response and
    /// the unique warning id so the user can actually diagnose the problem.
    /// </summary>
    internal sealed class ApiResponseException : Exception
    {
        public ApiResponseException(string message) : base(message)
        {
        }
    }

    internal enum ApiType
    {
        Auto,
        Responses,
        ChatCompletions
    }

    internal sealed class OpenAiClient
    {
        /// <summary>
        /// A single, process-wide HttpClient. Creating one HttpClient per request is a
        /// well-known .NET anti-pattern: disposed handlers keep sockets in TIME_WAIT,
        /// which can exhaust ephemeral ports, and every call pays a fresh TLS handshake.
        /// Per-request timeouts are applied with a CancellationTokenSource instead of
        /// HttpClient.Timeout, because the latter cannot be changed once a request starts.
        /// </summary>
        private static readonly HttpClient SharedClient = CreateClient();

        private readonly string _apiKey;
        private readonly string _model;
        private readonly string _baseUrl;
        private readonly string _organization;
        private readonly string _project;
        private readonly ApiType _apiType;
        private readonly string _systemPrompt;
        private readonly int _timeoutMs;

        public OpenAiClient(
            string apiKey,
            string model,
            string baseUrl,
            string organization,
            string project,
            string apiType,
            string systemPrompt,
            int? timeoutMs)
        {
            _apiKey = apiKey?.Trim();
            _model = string.IsNullOrWhiteSpace(model) ? "gpt-4.1" : model.Trim();
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://api.openai.com/v1"
                : baseUrl.Trim().TrimEnd('/');
            _organization = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim();
            _project = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
            _apiType = NormalizeApiType(apiType);
            _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim();
            _timeoutMs = timeoutMs.HasValue && timeoutMs.Value > 0 ? timeoutMs.Value : 60000;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public string Model => _model;

        public string BaseUrl => _baseUrl;

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var client = new HttpClient(handler)
            {
                // Effectively "no global timeout" - each request carries its own deadline.
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        /// <summary>
        /// Translates one or more texts. Batched requests are strongly preferred:
        /// a single HTTP round trip handles up to <paramref name="BatchSize"/> items.
        /// </summary>
        public string[] TranslateBatch(string[] inputs)
        {
            if (inputs == null || inputs.Length == 0)
            {
                return Array.Empty<string>();
            }

            if (inputs.Length == 1)
            {
                return new[] { TranslateSingle(inputs[0]) };
            }

            return TranslateManyInOneRequest(inputs);
        }

        public string TranslateSingle(string inputText)
        {
            switch (_apiType)
            {
                case ApiType.ChatCompletions:
                    return SendChatCompletion(inputText, expectJsonArray: false);
                case ApiType.Responses:
                    return SendResponses(inputText, expectJsonArray: false);
                case ApiType.Auto:
                default:
                    try
                    {
                        return SendResponses(inputText, expectJsonArray: false);
                    }
                    catch (ApiNotFoundException)
                    {
                        return SendChatCompletion(inputText, expectJsonArray: false);
                    }
            }
        }

        /// <summary>
        /// Sends every item in a single request and expects a JSON array back.
        /// If the model replies with something that is not a well-formed array of the
        /// expected length, the caller is told via <c>null</c> and is expected to
        /// fall back to one-by-one translation, so a malformed reply never loses text.
        /// </summary>
        private string[] TranslateManyInOneRequest(string[] inputs)
        {
            var payload = BuildBatchPayload(inputs);

            string raw;
            try
            {
                raw = _apiType == ApiType.ChatCompletions
                    ? SendChatCompletion(payload, expectJsonArray: true)
                    : SendResponses(payload, expectJsonArray: true);
            }
            catch (ApiNotFoundException)
            {
                if (_apiType == ApiType.ChatCompletions)
                {
                    throw;
                }

                raw = SendChatCompletion(payload, expectJsonArray: true);
            }

            var parsed = TryParseTranslationArray(raw, inputs.Length);
            return parsed;
        }

        public static string BuildBatchPayload(string[] inputs)
        {
            // Ask for a JSON array keyed by index. Index-keying removes any ordering
            // ambiguity should the model decide to reorder or drop an entry.
            var sb = new StringBuilder();
            sb.Append("Translate each element of the following JSON array from Chinese to natural, professional English.\n");
            sb.Append("Rules:\n");
            sb.Append("1. Keep the array length identical to the input.\n");
            sb.Append("2. Keep the same order; element i of the output must be the translation of element i of the input.\n");
            sb.Append("3. Preserve numbers, units, punctuation and line breaks exactly.\n");
            sb.Append("4. Preserve AutoCAD MText formatting codes such as \\\\P, \\\\L, \\\\l, \\\\O, \\\\o, \\\\S, \\\\A unchanged.\n");
            sb.Append("5. Text that is already English, or that contains no Chinese, must be returned unchanged.\n");
            sb.Append("6. Reply with ONLY a JSON array of strings. No markdown fences, no commentary.\n\n");
            sb.Append("Input:\n");
            sb.Append(JsonSerializer.Serialize(inputs));
            sb.Append("\n\nOutput:");
            return sb.ToString();
        }

        private string[] TryParseTranslationArray(string raw, int expectedLength)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var json = ExtractJsonArray(raw);
            if (json == null)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var result = new string[doc.RootElement.GetArrayLength()];
                var i = 0;
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    result[i++] = element.GetString();
                }

                return result.Length == expectedLength ? result : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Models occasionally wrap JSON in prose or markdown fences. Grab the outermost
        /// bracketed span; if there is not one, give up and let the caller fall back.
        /// </summary>
        private static string ExtractJsonArray(string raw)
        {
            var start = raw.IndexOf('[');
            var end = raw.LastIndexOf(']');
            if (start < 0 || end <= start)
            {
                return null;
            }

            return raw.Substring(start, end - start + 1);
        }

        private string SendResponses(string inputText, bool expectJsonArray)
        {
            var requestBody = new
            {
                model = _model,
                input = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[]
                        {
                            new { type = "input_text", text = inputText }
                        }
                    }
                }
            };

            var url = $"{_baseUrl}/responses";
            try
            {
                return SendRequest(url, requestBody, ExtractOutputText, expectJsonArray);
            }
            catch (ApiNotFoundException) when (expectJsonArray)
            {
                // Fall back to Chat Completions when the gateway has no /responses route.
                var fallbackBase = TrimV1Suffix(_baseUrl);
                if (!string.Equals(fallbackBase, _baseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return SendRequest($"{fallbackBase}/chat/completions", BuildChatBody(inputText), ExtractChatText, true);
                }

                throw;
            }
        }

        private string SendChatCompletion(string inputText, bool expectJsonArray)
        {
            var url = $"{_baseUrl}/chat/completions";
            try
            {
                return SendRequest(url, BuildChatBody(inputText), ExtractChatText, expectJsonArray);
            }
            catch (ApiNotFoundException)
            {
                var fallbackBase = TrimV1Suffix(_baseUrl);
                if (!string.Equals(fallbackBase, _baseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return SendRequest($"{fallbackBase}/chat/completions", BuildChatBody(inputText), ExtractChatText, expectJsonArray);
                }

                throw;
            }
        }

        private object BuildChatBody(string inputText)
        {
            return new
            {
                model = _model,
                temperature = 0.0,
                messages = BuildChatMessages(inputText),
                stream = false
            };
        }

        private string SendRequest(
            string url,
            object requestBody,
            Func<string, string> extract,
            bool expectJsonArray)
        {
            var json = JsonSerializer.Serialize(requestBody);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            if (!string.IsNullOrWhiteSpace(_organization))
            {
                request.Headers.Add("OpenAI-Organization", _organization);
            }

            if (!string.IsNullOrWhiteSpace(_project))
            {
                request.Headers.Add("OpenAI-Project", _project);
            }

            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_timeoutMs));

            string content;
            HttpStatusCode statusCode;
            string reasonPhrase;

            try
            {
                using var response = SharedClient
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                    .GetAwaiter()
                    .GetResult();

                content = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
                statusCode = response.StatusCode;
                reasonPhrase = response.ReasonPhrase;
            }
            catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Request to {url} timed out after {_timeoutMs} ms. " +
                    "Raise OPENAI_TIMEOUT_MS or check network connectivity.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Could not reach {url}: {ex.Message}. " +
                    "Check OPENAI_BASE_URL and your network/proxy settings.", ex);
            }

            if (statusCode == HttpStatusCode.NotFound)
            {
                throw new ApiNotFoundException(
                    $"Endpoint not found (404): {url}. " +
                    "For gateways such as DeepSeek, set OPENAI_API_TYPE=chat_completions " +
                    "and do not append /v1 to OPENAI_BASE_URL. " +
                    $"Server said: {Truncate(content)}");
            }

            if ((int)statusCode >= 400)
            {
                throw new InvalidOperationException(
                    $"API error {(int)statusCode} {reasonPhrase} from {url}. Server said: {Truncate(content)}");
            }

            var extracted = extract(content);
            if (string.IsNullOrWhiteSpace(extracted) && !expectJsonArray)
            {
                throw new ApiResponseException(
                    $"The model returned no usable text for {url}. Raw response: {Truncate(content)}");
            }

            return extracted;
        }

        /// <summary>
        /// Extracts plain text from a Responses API payload.
        /// Throws with the raw payload attached when the shape is unrecognised, so the
        /// failure mode is diagnosable rather than a bare JsonException.
        /// </summary>
        private static string ExtractOutputText(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new ApiResponseException(
                    $"Responses API returned a non-JSON body ({ex.Message}). Raw response: {Truncate(json)}");
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("error", out var errorElement))
                    {
                        throw new ApiResponseException(
                            $"The API reported an error: {errorElement.ToString()}. Raw response: {Truncate(json)}");
                    }

                    if (root.TryGetProperty("output_text", out var outputTextElement) &&
                        outputTextElement.ValueKind == JsonValueKind.String)
                    {
                        return outputTextElement.GetString()?.Trim() ?? string.Empty;
                    }

                    if (root.TryGetProperty("output", out var outputElement) &&
                        outputElement.ValueKind == JsonValueKind.Array)
                    {
                        var builder = new StringBuilder();
                        foreach (var item in outputElement.EnumerateArray())
                        {
                            if (!item.TryGetProperty("content", out var contentElement) ||
                                contentElement.ValueKind != JsonValueKind.Array)
                            {
                                continue;
                            }

                            foreach (var entry in contentElement.EnumerateArray())
                            {
                                if (!entry.TryGetProperty("type", out var typeElement) ||
                                    typeElement.ValueKind != JsonValueKind.String)
                                {
                                    continue;
                                }

                                if (!string.Equals(typeElement.GetString(), "output_text", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                if (!entry.TryGetProperty("text", out var textElement) ||
                                    textElement.ValueKind != JsonValueKind.String)
                                {
                                    continue;
                                }

                                var text = textElement.GetString();
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    if (builder.Length > 0)
                                    {
                                        builder.Append('\n');
                                    }

                                    builder.Append(text.Trim());
                                }
                            }
                        }

                        return builder.ToString().Trim();
                    }
                }

                throw new ApiResponseException(
                    $"Unrecognised Responses API payload (unexpected shape). Raw response: {Truncate(json)}");
            }
        }

        private static string ExtractChatText(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new ApiResponseException(
                    $"Chat Completions API returned a non-JSON body ({ex.Message}). Raw response: {Truncate(json)}");
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new ApiResponseException(
                        $"Unrecognised Chat Completions payload. Raw response: {Truncate(json)}");
                }

                if (root.TryGetProperty("error", out var errorElement))
                {
                    throw new ApiResponseException(
                        $"The API reported an error: {errorElement.ToString()}. Raw response: {Truncate(json)}");
                }

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    throw new ApiResponseException(
                        $"Chat Completions response contained no 'choices'. Raw response: {Truncate(json)}");
                }

                var first = choices[0];
                if (first.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString()?.Trim() ?? string.Empty;
                }

                if (first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString()?.Trim() ?? string.Empty;
                }

                throw new ApiResponseException(
                    $"Chat Completions choice contained no message content. Raw response: {Truncate(json)}");
            }
        }

        private object[] BuildChatMessages(string inputText)
        {
            if (string.IsNullOrWhiteSpace(_systemPrompt))
            {
                return new object[]
                {
                    new { role = "user", content = inputText }
                };
            }

            return new object[]
            {
                new { role = "system", content = _systemPrompt },
                new { role = "user", content = inputText }
            };
        }

        private static string TrimV1Suffix(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return baseUrl;
            }

            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                return baseUrl.Substring(0, baseUrl.Length - 3);
            }

            return baseUrl;
        }

        internal static ApiType NormalizeApiType(string apiType)
        {
            if (string.IsNullOrWhiteSpace(apiType))
            {
                return ApiType.Auto;
            }

            var value = apiType.Trim().ToLowerInvariant();
            if (value == "responses" || value == "response")
            {
                return ApiType.Responses;
            }

            if (value == "chat" || value == "chat_completions" || value == "chat-completions")
            {
                return ApiType.ChatCompletions;
            }

            return ApiType.Auto;
        }

        internal static string Truncate(string value, int maxLength = 500)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "<empty>";
            }

            var flattened = value.Replace("\r", " ").Replace("\n", " ");
            return flattened.Length <= maxLength
                ? flattened
                : flattened.Substring(0, maxLength) + "...";
        }
    }
}
