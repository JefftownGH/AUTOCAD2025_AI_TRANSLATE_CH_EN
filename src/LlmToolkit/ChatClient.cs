using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LlmToolkit
{
    /// <summary>
    /// A client for OpenAI-compatible chat APIs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "OpenAI-compatible" describes the wire format, not the vendor. Zhipu/BigModel,
    /// DeepSeek, DashScope (Qwen), Moonshot, Volcengine Ark and SiliconFlow all expose
    /// the same <c>POST /chat/completions</c> shape with a bearer token, and local
    /// runtimes such as Ollama and vLLM do too. This client speaks that shape and
    /// nothing else, so it works against any of them without a vendor SDK -- which
    /// matters for an audience where the vendor's own hosted endpoint is unreachable.
    /// </para>
    /// <para>
    /// This type is deliberately free of any domain knowledge. It sends a system prompt
    /// and a user message and returns text; what that text means is the caller's
    /// business. The original implementation had translation baked into the transport
    /// (it built the batch prompt, parsed a JSON array and hardcoded a target language),
    /// which made it impossible to reuse for any other task.
    /// </para>
    /// </remarks>
    public sealed class ChatClient
    {
        /// <summary>
        /// A single, process-wide HttpClient per endpoint configuration. Creating one
        /// HttpClient per request is a well-known .NET anti-pattern: disposed handlers
        /// keep sockets in TIME_WAIT, which can exhaust ephemeral ports, and every call
        /// pays a fresh TLS handshake.
        /// </summary>
        /// <remarks>
        /// Per-request timeouts are applied with a CancellationTokenSource instead of
        /// HttpClient.Timeout, because the latter cannot be changed once a request starts
        /// and would otherwise have to be baked in at construction.
        /// </remarks>
        private static readonly HttpClient SharedClient = CreateClient();

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
        /// Maximum attempts for a transient failure. Deliberately small: an interactive
        /// run should retry the common "unexpected EOF" case without making a genuinely
        /// dead endpoint feel hung.
        /// </summary>
        private const int MaxAttempts = 3;

        private static readonly Random Random = new Random();

        private readonly LlmOptions _options;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly string _baseUrl;
        private readonly string _organization;
        private readonly string _project;
        private readonly ApiType _apiType;
        private readonly string _systemPrompt;
        private readonly int _timeoutMs;
        private readonly EndpointProbeStore _probeStore;
        private readonly ILlmDiagnostics _diagnostics;

        /// <summary>
        /// Which endpoint family Auto mode settled on. Seeded from the persisted probe
        /// cache at construction, because the instance is short-lived: a settings dialog
        /// builds a fresh client for every "test connection" click. A per-instance cache
        /// would therefore always be cold and every click would pay a doomed
        /// <c>/responses</c> round trip -- which, when the network hiccups during that
        /// wasted request, surfaces as a confusing "SSL connection could not be
        /// established" error.
        /// </summary>
        private ApiType _resolvedAutoType = ApiType.Auto;

        /// <summary>
        /// Optional override for the system prompt sent with each request. Lets a caller
        /// vary the instruction per run -- a translation target language, for instance --
        /// without rebuilding the client and re-probing the endpoint.
        /// </summary>
        private string _systemPromptOverride;

        public ChatClient(
            LlmOptions options,
            EndpointProbeStore probeStore = null,
            ILlmDiagnostics diagnostics = null)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _options = options;
            _diagnostics = diagnostics ?? NullDiagnostics.Instance;

            _apiKey = options.ApiKey?.Trim();
            _model = string.IsNullOrWhiteSpace(options.Model) ? LlmOptions.DefaultModel : options.Model.Trim();
            _baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? LlmOptions.DefaultBaseUrl
                : options.BaseUrl.Trim().TrimEnd('/');
            _organization = string.IsNullOrWhiteSpace(options.Organization) ? null : options.Organization.Trim();
            _project = string.IsNullOrWhiteSpace(options.Project) ? null : options.Project.Trim();
            _apiType = NormalizeApiType(options.ApiType);
            _systemPrompt = string.IsNullOrWhiteSpace(options.SystemPrompt) ? null : options.SystemPrompt.Trim();
            _timeoutMs = options.TimeoutMs.HasValue && options.TimeoutMs.Value > 0
                ? options.TimeoutMs.Value
                : LlmOptions.DefaultTimeoutMs;

            _probeStore = probeStore;

            // Warm the probe cache from disk. Only meaningful in Auto mode; an explicit
            // apiType already answers the question.
            if (_apiType == ApiType.Auto && _probeStore != null)
            {
                var remembered = _probeStore.Lookup(_baseUrl);
                if (remembered.HasValue)
                {
                    _resolvedAutoType = remembered.Value;
                }
            }
        }

        /// <summary>Whether an API key was supplied.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public string Model => _model;

        public string BaseUrl => _baseUrl;

        /// <summary>The endpoint family Auto mode settled on, or is heading for.</summary>
        public ApiType ResolvedApiType => _apiType == ApiType.Auto ? _resolvedAutoType : _apiType;

        /// <summary>
        /// Replaces the system prompt for subsequent requests. Unlike rebuilding the
        /// client this preserves the resolved endpoint, so switching instructions mid-run
        /// does not pay another probe.
        /// </summary>
        public void SetSystemPrompt(string systemPrompt)
        {
            _systemPromptOverride = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim();
        }

        /// <summary>
        /// Sends a single user message and returns the model's reply as plain text.
        /// </summary>
        /// <param name="inputText">The user turn.</param>
        public string Send(string inputText)
        {
            return Send(inputText, expectContent: true);
        }

        /// <summary>
        /// Sends a single user message.
        /// </summary>
        /// <param name="inputText">The user turn.</param>
        /// <param name="expectContent">
        /// When false, an empty reply is returned rather than raising
        /// <see cref="ApiResponseException"/>. Some legitimate prompts (a strict
        /// "return only a JSON array" instruction that the model answers with an empty
        /// array) produce a response the caller wants to validate itself.
        /// </param>
        public string Send(string inputText, bool expectContent)
        {
            var effectivePrompt = _systemPromptOverride ?? _systemPrompt;

            switch (_apiType)
            {
                case ApiType.ChatCompletions:
                    return SendChatCompletion(inputText, effectivePrompt, expectContent);

                case ApiType.Responses:
                    return SendResponses(inputText, effectivePrompt, expectContent);

                default:
                    return SendAuto(inputText, effectivePrompt, expectContent);
            }
        }

        /// <summary>
        /// Sends several independent user messages, one request each, returning replies
        /// in the same order. A null entry in the result means that item failed; the
        /// caller decides whether that is fatal.
        /// </summary>
        /// <remarks>
        /// Sequential on purpose. Concurrency is a policy the caller owns -- it knows how
        /// many requests a given gateway tolerates and how to surface progress -- so this
        /// method stays a simple loop rather than imposing a thread count.
        /// </remarks>
        public string[] SendMany(string[] inputs)
        {
            if (inputs == null || inputs.Length == 0)
            {
                return Array.Empty<string>();
            }

            var results = new string[inputs.Length];
            for (var i = 0; i < inputs.Length; i++)
            {
                results[i] = Send(inputs[i]);
            }

            return results;
        }

        private string SendAuto(string inputText, string systemPrompt, bool expectContent)
        {
            var type = _resolvedAutoType;

            if (type == ApiType.Auto)
            {
                // Nothing recorded: try the richer /responses shape first, since it is
                // the one that carries an explicit instructions field.
                try
                {
                    var viaResponses = SendResponses(inputText, systemPrompt, expectContent);

                    // Only the Responses API can produce this exception, and reaching
                    // here without it proves the route exists.
                    ResolveAutoType(ApiType.Responses);
                    return viaResponses;
                }
                catch (ApiNotFoundException)
                {
                    // The gateway has no /responses route. This is the normal case for
                    // Zhipu/BigModel, DeepSeek and most OpenAI-compatible proxies, which
                    // only implement Chat Completions. Retry on the same base URL:
                    // deliberately NOT gated on a '/v1' suffix check, because gateways
                    // commonly version their path as /v4 (Zhipu) or /v3 (others), and a
                    // suffix check would silently suppress this fallback.
                    ResolveAutoType(ApiType.ChatCompletions);
                }
            }
            else if (type == ApiType.ChatCompletions)
            {
                // A recorded ChatCompletions that starts 404ing means the gateway changed
                // (a route was removed, or the host now serves something else entirely).
                // Drop the record first: if the fallback below also fails, the bad entry
                // is already gone and the next call probes from scratch rather than
                // replaying a known-dead answer forever.
                try
                {
                    return SendChatCompletion(inputText, systemPrompt, expectContent);
                }
                catch (ApiNotFoundException)
                {
                    ForgetResolvedEndpoint();
                    throw;
                }
            }

            var viaChat = SendChatCompletion(inputText, systemPrompt, expectContent);

            // Reaching here proves Chat Completions works on this base URL, so the
            // in-memory answer is refreshed either way. That covers the seeded case
            // (type was already ChatCompletions) without a redundant disk write, since
            // ResolveAutoType short-circuits on an unchanged value and Remember itself
            // skips a file write when the entry already matches.
            ResolveAutoType(ApiType.ChatCompletions);

            return viaChat;
        }

        /// <summary>
        /// Records the endpoint family that answered, and persists it through the probe
        /// store when the answer changed in memory.
        /// </summary>
        /// <remarks>
        /// The stale-entry case is handled at the catch site in <see cref="SendAuto"/>,
        /// not here, because a 404 on a *remembered* endpoint means the record is wrong
        /// and has to be dropped -- whereas a successful call means it is right and only
        /// needs refreshing. Folding both into this method would be shorter but would
        /// make the two intentions ('remember this' vs 'forget that') indistinguishable
        /// at the call site.
        /// </remarks>
        private void ResolveAutoType(ApiType resolved)
        {
            if (resolved == _resolvedAutoType)
            {
                return;
            }

            _resolvedAutoType = resolved;
            _probeStore?.Remember(_baseUrl, resolved);
        }

        /// <summary>
        /// Drops a remembered endpoint that has stopped working, so the next call probes
        /// afresh instead of replaying a recorded answer the gateway no longer serves.
        /// </summary>
        /// <remarks>
        /// Without this the cache is a one-way ratchet: once a base URL is recorded as
        /// ChatCompletions, a gateway that later gains or drops a route stays permanently
        /// mis-remembered, and every request pays a doomed attempt before falling back.
        /// The probe store's own comments describe this self-healing behaviour, so the
        /// client is the side that has to invoke it.
        /// </remarks>
        private void ForgetResolvedEndpoint()
        {
            if (_resolvedAutoType == ApiType.Auto)
            {
                return;
            }

            _resolvedAutoType = ApiType.Auto;
            _probeStore?.Forget(_baseUrl);
        }

        private string SendResponses(string inputText, string systemPrompt, bool expectContent)
        {
            // The Responses payload has no system-role slot in this shape, so any
            // instruction has to travel in the `instructions` field. Omitting it makes
            // the endpoint fall back to guessing, with no error signal.
            var requestBody = new
            {
                model = _model,
                instructions = systemPrompt ?? string.Empty,
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
            return SendRequest(url, requestBody, ExtractOutputText, expectContent);
        }

        private string SendChatCompletion(string inputText, string systemPrompt, bool expectContent)
        {
            var url = $"{_baseUrl}/chat/completions";
            var body = new
            {
                model = _model,
                temperature = 0.0,
                messages = BuildChatMessages(inputText, systemPrompt),
                stream = false
            };

            return SendRequest(url, body, ExtractChatText, expectContent);
        }

        private object[] BuildChatMessages(string inputText, string systemPrompt)
        {
            // A system turn is always emitted, even when empty. Some gateways reject a
            // request whose messages array has only one entry, and an explicit empty
            // system turn is well-defined by the wire format.
            return new object[]
            {
                new { role = "system", content = systemPrompt ?? string.Empty },
                new { role = "user", content = inputText }
            };
        }

        /// <summary>
        /// Sends a request, retrying transient transport and server failures.
        /// </summary>
        /// <remarks>
        /// "Received an unexpected EOF or 0 bytes from the transport stream" is the
        /// canonical intermittent failure: the TCP/TLS stream is torn down by a
        /// middlebox, a load balancer or a pooled socket the peer already closed. It is
        /// not deterministic, so the correct remedy is a bounded retry rather than a
        /// configuration change. A fresh request object and a fresh
        /// CancellationTokenSource are built for every attempt, because both are
        /// single-use once sent.
        /// </remarks>
        private string SendRequest(
            string url,
            object requestBody,
            Func<string, string> extract,
            bool expectContent)
        {
            Exception lastError = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    return SendRequestOnce(url, requestBody, extract, expectContent);
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
                {
                    lastError = ex;

                    // Exponential backoff with jitter: 400ms, 800ms (+/- 25%). Jitter
                    // matters because a caller may issue several requests in parallel,
                    // and identical backoff would make them retry in lockstep.
                    var baseDelay = 400 * (1 << (attempt - 1));
                    var jitter = Random.Next(baseDelay / 4 + 1);
                    var delay = baseDelay + jitter;

                    _diagnostics.Log(
                        $"attempt {attempt}/{MaxAttempts} to {url} failed transiently " +
                        $"({ex.GetType().Name}: {ex.Message}); retrying in {delay} ms");

                    Thread.Sleep(delay);
                }
            }

            // Unreachable: the loop either returns or throws on the final attempt.
            throw lastError ?? new InvalidOperationException($"Request to {url} failed.");
        }

        /// <summary>
        /// True when retrying the same request has a realistic chance of succeeding.
        /// </summary>
        /// <remarks>
        /// Classification is by exception TYPE, never by message text. An earlier
        /// revision matched substrings such as "EOF" inside the inner exception's
        /// message, which meant a retryable 429 -- whose synthetic inner exception said
        /// "retryable", not "EOF" -- was silently classified as permanent and never
        /// retried. Type-based checks cannot rot that way.
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

                // The per-request deadline already elapsed. Retrying would burn another
                // full timeout on a server that is simply too slow.
                case TimeoutException _:
                    return false;

                // A bare transport failure (the SendRequestOnce wrapper always nests the
                // HttpRequestException, but be defensive).
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
        /// True when the inner exception describes a transport-layer failure rather than
        /// a clean HTTP error response.
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

            // "Received an unexpected EOF or 0 bytes from the transport stream" is an
            // IOException. Any IOException on the transport path is a torn-down stream,
            // which a fresh connection may well survive.
            return inner is IOException;
        }

        private string SendRequestOnce(
            string url,
            object requestBody,
            Func<string, string> extract,
            bool expectContent)
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
                    $"Raise {LlmSettingKeys.TimeoutMs} or check network connectivity.", ex);
            }
            catch (HttpRequestException ex)
            {
                // HttpRequestException covers everything below the HTTP layer: DNS
                // failure, TCP refusal, TLS handshake failure and connection reset.
                // Surface the inner exception, because "Could not reach X" on its own
                // sends people looking at the wrong layer -- an SSL handshake error
                // against a healthy host is usually a proxy (HTTPS_PROXY) that cannot
                // CONNECT to that particular hostname.
                var inner = ex.InnerException;
                var detail = inner == null
                    ? ex.Message
                    : $"{ex.Message} (inner: {inner.GetType().Name}: {inner.Message})";

                throw new InvalidOperationException(
                    $"Could not reach {url}: {detail}. " +
                    $"Connectivity checklist: (1) confirm {LlmSettingKeys.BaseUrl} is correct; " +
                    "(2) if you are behind a proxy, HTTPS_PROXY must be able to CONNECT to this host; " +
                    "(3) note that 404/timeout responses from the API are reported separately " +
                    "and are NOT network failures.", ex);
            }

            if (statusCode == HttpStatusCode.NotFound)
            {
                throw new ApiNotFoundException(
                    $"Endpoint not found (404): {url}. " +
                    "This gateway does not implement that route. " +
                    "Most OpenAI-compatible gateways only serve /chat/completions, so set " +
                    $"{LlmSettingKeys.ApiType}=chat_completions. " +
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
            if (string.IsNullOrWhiteSpace(extracted) && expectContent)
            {
                throw new ApiResponseException(
                    $"The model returned no usable text for {url}. Raw response: {Truncate(content)}");
            }

            return extracted;
        }

        /// <summary>
        /// Extracts plain text from a Responses API payload. Throws with the raw payload
        /// attached when the shape is unrecognised, so the failure mode is diagnosable
        /// rather than a bare JsonException.
        /// </summary>
        public static string ExtractOutputText(string json)
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
                            $"The API reported an error: {errorElement}. Raw response: {Truncate(json)}");
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

                                if (!string.Equals(
                                        typeElement.GetString(), "output_text", StringComparison.OrdinalIgnoreCase))
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

        /// <summary>Extracts plain text from a Chat Completions payload.</summary>
        public static string ExtractChatText(string json)
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
                        $"The API reported an error: {errorElement}. Raw response: {Truncate(json)}");
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

        /// <summary>Parses a free-text API type into the enum, defaulting to Auto.</summary>
        public static ApiType NormalizeApiType(string apiType)
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

        /// <summary>Flattens and shortens a value for inclusion in an error message.</summary>
        public static string Truncate(string value, int maxLength = 500)
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

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,

                // Honour HTTP_PROXY / HTTPS_PROXY / NO_PROXY. This is the default, but it
                // is stated explicitly because a surprising number of TLS failures
                // ("unexpected EOF", "connection reset") are a proxy that cannot reach
                // the target host, and silently disabling proxy support would turn a
                // working corporate setup into a broken one.
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy,
            };

            // The pool must outlive the client: with Timeout=InfiniteTimeSpan and a
            // long-lived static client, connections are reused, so a stale pooled socket
            // that the peer already closed is a real failure mode. Keep the connection
            // limit generous enough to benefit from reuse.
            ServicePointManager.DefaultConnectionLimit =
                Math.Max(8, ServicePointManager.DefaultConnectionLimit);

            var client = new HttpClient(handler)
            {
                // Effectively "no global timeout" - each request carries its own deadline.
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.ConnectionClose = false;
            return client;
        }
    }
}
