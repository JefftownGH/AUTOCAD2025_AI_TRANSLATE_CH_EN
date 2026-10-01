using System;
using System.IO;
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

    /// <summary>
    /// Signals a failure that is worth retrying: a stalled/aborted transport stream
    /// or a transient HTTP status (408/429/5xx).
    /// </summary>
    /// <remarks>
    /// This exists as a dedicated type rather than as a message convention on purpose.
    /// An earlier revision tagged retryable HTTP statuses by wrapping an
    /// <see cref="IOException"/> whose *message text* said "retryable". Classification
    /// then depended on substring matching, so the text never matched any of the
    /// transport keywords and 429 responses were never retried -- the retry logic
    /// silently did nothing for the exact case it was added for. Type-based
    /// classification removes that whole failure mode.
    /// </remarks>
    internal sealed class TransientApiException : Exception
    {
        public TransientApiException(string message) : base(message)
        {
        }

        public TransientApiException(string message, Exception innerException)
            : base(message, innerException)
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

        /// <summary>
        /// Which endpoint family Auto mode settled on. Seeded from the persisted
        /// probe cache at construction, because the instance is short-lived: the
        /// settings dialog builds a fresh client for every "test connection" click.
        /// A per-instance cache would therefore always be cold and every click would
        /// pay a doomed <c>/responses</c> round trip -- which, when the network
        /// hiccups during that wasted request, surfaces as a confusing
        /// "SSL connection could not be established" error.
        /// </summary>
        private ApiType _resolvedAutoType = ApiType.Auto;

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

            // Warm the probe cache from disk. Only meaningful in Auto mode; an
            // explicit apiType already answers the question.
            if (_apiType == ApiType.Auto)
            {
                var remembered = EndpointProbeStore.Lookup(_baseUrl);
                if (remembered.HasValue)
                {
                    _resolvedAutoType = remembered.Value;
                }
            }
        }

        /// <summary>
        /// Records the endpoint that worked, both in memory and on disk, so future
        /// clients pointed at the same gateway skip the probe entirely.
        /// </summary>
        private void ResolveAutoType(ApiType resolved)
        {
            _resolvedAutoType = resolved;
            EndpointProbeStore.Remember(_baseUrl, resolved);
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public string Model => _model;

        public string BaseUrl => _baseUrl;

        /// <summary>
        /// Status codes worth retrying. 408/429/5xx are transient by definition.
        /// 404 is NOT here - it means the route does not exist and retrying is pointless.
        /// </summary>
        private static readonly HttpStatusCode[] RetryableStatusCodes =
        {
            HttpStatusCode.RequestTimeout,      // 408
            (HttpStatusCode)429,                // Too Many Requests
            HttpStatusCode.InternalServerError, // 500
            HttpStatusCode.BadGateway,          // 502
            HttpStatusCode.ServiceUnavailable,  // 503
            HttpStatusCode.GatewayTimeout       // 504
        };

        /// <summary>
        /// Maximum attempts for a transient failure. Deliberately small: a translation
        /// run is interactive, and three attempts with backoff covers the common
        /// "unexpected EOF" case without making a genuinely dead endpoint feel hung.
        /// </summary>
        private const int MaxAttempts = 3;

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,

                // Honour HTTP_PROXY / HTTPS_PROXY / NO_PROXY. This is the default, but
                // it is stated explicitly because a surprising number of TLS failures
                // ("unexpected EOF", "connection reset") are a proxy that cannot reach
                // the target host, and silently disabling proxy support would turn a
                // working corporate setup into a broken one.
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy,
            };

            // The pool must outlive the client: with Timeout=InfiniteTimeSpan and a
            // long-lived static client, connections are reused, so a stale pooled
            // socket that the peer already closed is a real failure mode. Keep the
            // lifetime short enough that dead sockets get recycled, long enough to
            // benefit from reuse. Also bound the connect phase separately, otherwise
            // a blackholed host burns the entire per-request deadline.
            ServicePointManager.DefaultConnectionLimit = Math.Max(8, ServicePointManager.DefaultConnectionLimit);

            var client = new HttpClient(handler)
            {
                // Effectively "no global timeout" - each request carries its own deadline.
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.ConnectionClose = false;
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
                    // Remember the outcome of the probe so a drawing with 500 texts
                    // does not pay 500 doomed /responses round trips, and so the next
                    // client (the settings dialog builds a fresh one per click) starts
                    // already knowing the answer.
                    if (_resolvedAutoType == ApiType.ChatCompletions)
                    {
                        return SendChatCompletion(inputText, expectJsonArray: false);
                    }

                    try
                    {
                        var viaResponses = SendResponses(inputText, expectJsonArray: false);
                        ResolveAutoType(ApiType.Responses);
                        return viaResponses;
                    }
                    catch (ApiNotFoundException)
                    {
                        ResolveAutoType(ApiType.ChatCompletions);
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
                if (_apiType == ApiType.ChatCompletions ||
                    _resolvedAutoType == ApiType.ChatCompletions)
                {
                    raw = SendChatCompletion(payload, expectJsonArray: true);
                    ResolveAutoType(ApiType.ChatCompletions);
                }
                else
                {
                    raw = SendResponses(payload, expectJsonArray: true);
                    ResolveAutoType(_apiType == ApiType.Auto ? ApiType.Responses : _apiType);
                }
            }
            catch (ApiNotFoundException)
            {
                if (_apiType == ApiType.ChatCompletions)
                {
                    throw;
                }

                // Same reasoning as SendResponses: fall back on the same base URL
                // rather than on a '/v1'-stripped variant.
                raw = SendChatCompletion(payload, expectJsonArray: true);
                ResolveAutoType(ApiType.ChatCompletions);
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
                // expectJsonArray records whether this call can be conversationally
                // retried by the caller; ApiNotFoundException is deterministic either
                // way and never retried inside SendRequest.
                return SendRequest(url, requestBody, ExtractOutputText, expectJsonArray);
            }
            catch (ApiNotFoundException)
            {
                // The gateway has no /responses route. This is the normal case for
                // Zhipu/BigModel, DeepSeek and most OpenAI-compatible proxies, which
                // only implement Chat Completions. Retry on the same base URL:
                // deliberately NOT gated on a '/v1' suffix check, because gateways
                // commonly version their path as /v4 (Zhipu) or /v3 (others), and a
                // suffix check would silently suppress this fallback.
                return SendRequest(
                    $"{_baseUrl}/chat/completions",
                    BuildChatBody(inputText),
                    ExtractChatText,
                    expectJsonArray);
            }
        }

        private string SendChatCompletion(string inputText, bool expectJsonArray)
        {
            var url = $"{_baseUrl}/chat/completions";
            return SendRequest(url, BuildChatBody(inputText), ExtractChatText, expectJsonArray);
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

        /// <summary>
        /// Sends a request, retrying transient transport and server failures.
        /// </summary>
        /// <remarks>
        /// "Received an unexpected EOF or 0 bytes from the transport stream" is the
        /// canonical intermittent failure: the TCP/TLS stream is torn down by a
        /// middlebox, a load balancer or a pooled socket the peer already closed.
        /// It is not deterministic, so the correct remedy is a bounded retry rather
        /// than a configuration change. A fresh request object and a fresh
        /// CancellationTokenSource are built for every attempt, because both are
        /// single-use once sent.
        /// </remarks>
        private string SendRequest(
            string url,
            object requestBody,
            Func<string, string> extract,
            bool expectJsonArray)
        {
            Exception lastError = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    return SendRequestOnce(url, requestBody, extract, expectJsonArray);
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
                {
                    lastError = ex;

                    // Exponential backoff with jitter: 400ms, 800ms (+/- 25%).
                    // Jitter matters because a drawing translates several chunks in
                    // parallel, and identical backoff would make them retry in lockstep.
                    var baseDelay = 400 * (1 << (attempt - 1));
                    var jitter = Random.Next(baseDelay / 4 + 1);
                    var delay = baseDelay + jitter;

                    Diagnostics.Log(
                        $"attempt {attempt}/{MaxAttempts} to {url} failed transiently " +
                        $"({ex.GetType().Name}: {ex.Message}); retrying in {delay} ms");

                    Thread.Sleep(delay);
                }
            }

            // Unreachable: the loop either returns or throws on the final attempt.
            throw lastError ?? new InvalidOperationException($"Request to {url} failed.");
        }

        private static readonly Random Random = new Random();

        /// <summary>
        /// True when retrying the same request has a realistic chance of succeeding.
        /// </summary>
        /// <remarks>
        /// Classification is by exception TYPE, never by message text. An earlier
        /// revision matched substrings such as "EOF" inside the inner exception's
        /// message, which meant a retryable 429 -- whose synthetic inner exception
        /// said "retryable", not "EOF" -- was silently classified as permanent and
        /// never retried. Type-based checks cannot rot that way.
        /// </remarks>
        private static bool IsTransient(Exception ex)
        {
            switch (ex)
            {
                // Explicitly marked retryable: 408/429/5xx.
                case TransientApiException _:
                    return true;

                // Route absent, unparseable payload, bad request: deterministic.
                case ApiNotFoundException _:
                case ApiResponseException _:
                    return false;

                // The per-request deadline already elapsed. Retrying would burn
                // another full timeout on a server that is simply too slow.
                case TimeoutException _:
                    return false;

                // A bare transport failure (the SendRequestOnce wrapper always nests
                // the HttpRequestException, but be defensive).
                case HttpRequestException _:
                case IOException _:
                case System.Net.Sockets.SocketException _:
                    return true;

                case InvalidOperationException _:
                    // Thrown by SendRequestOnce for non-retryable HTTP 4xx, and also
                    // wraps HttpRequestException for transport failures. Inspect the
                    // inner exception to tell them apart.
                    var inner = ex.InnerException;
                    return inner != null && IsTransportFailure(inner);

                default:
                    return false;
            }
        }

        /// <summary>
        /// True when the inner exception describes a transport-layer failure rather
        /// than a clean HTTP error response.
        /// </summary>
        private static bool IsTransportFailure(Exception inner)
        {
            // HttpRequestException covers DNS / TCP / TLS / reset.
            if (inner is HttpRequestException ||
                inner is System.Net.Sockets.SocketException ||
                inner is System.Security.Authentication.AuthenticationException)
            {
                return true;
            }

            // "Received an unexpected EOF or 0 bytes from the transport stream" is
            // an IOException. Any IOException on the transport path is a torn-down
            // stream, which a fresh connection may well survive.
            return inner is IOException;
        }

        private string SendRequestOnce(
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
                // HttpRequestException covers everything below the HTTP layer:
                // DNS failure, TCP refusal, TLS handshake failure and connection
                // reset. Surface the inner exception, because "Could not reach X"
                // on its own sends people looking at the wrong layer -- an SSL
                // handshake error against a healthy host is usually a proxy
                // (HTTPS_PROXY) that cannot CONNECT to that particular hostname.
                var inner = ex.InnerException;
                var detail = inner == null
                    ? ex.Message
                    : $"{ex.Message} (inner: {inner.GetType().Name}: {inner.Message})";

                throw new InvalidOperationException(
                    $"Could not reach {url}: {detail}. " +
                    "Connectivity checklist: (1) confirm OPENAI_BASE_URL is correct; " +
                    "(2) if you are behind a proxy, HTTPS_PROXY must be able to CONNECT to this host; " +
                    "(3) note that 404/timeout responses from the API are reported separately " +
                    "and are NOT network failures.", ex);
            }

            if (statusCode == HttpStatusCode.NotFound)
            {
                throw new ApiNotFoundException(
                    $"Endpoint not found (404): {url}. " +
                    "This gateway does not implement that route. " +
                    "Zhipu/BigModel, DeepSeek and most OpenAI-compatible proxies only " +
                    "serve /chat/completions, so set OPENAI_API_TYPE=chat_completions. " +
                    $"Server said: {Truncate(content)}");
            }

            if (Array.IndexOf(RetryableStatusCodes, statusCode) >= 0)
            {
                throw new TransientApiException(
                    $"Transient API error {(int)statusCode} {reasonPhrase} from {url}. " +
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
