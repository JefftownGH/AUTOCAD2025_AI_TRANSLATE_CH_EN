using System;

namespace LlmToolkit
{
    /// <summary>
    /// Thrown when the server responds with 404, i.e. the requested route does not
    /// exist on the configured base URL. Drives the Responses -> ChatCompletions
    /// fallback.
    /// </summary>
    /// <remarks>
    /// A dedicated type rather than an HTTP status check at the call site, because the
    /// status alone cannot distinguish "this gateway has no /responses route" from
    /// "the user mistyped the base URL and nothing is there".
    /// </remarks>
    public sealed class ApiNotFoundException : Exception
    {
        public ApiNotFoundException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Thrown when the API responded successfully but the payload could not be parsed
    /// or contained no usable text. The message carries a truncated copy of the raw
    /// response so the caller can actually diagnose the problem.
    /// </summary>
    public sealed class ApiResponseException : Exception
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
    /// <see cref="System.IO.IOException"/> whose *message text* said "retryable".
    /// Classification then depended on substring matching, so the text never matched
    /// any of the transport keywords and 429 responses were never retried -- the retry
    /// logic silently did nothing for the exact case it was added for. Type-based
    /// classification removes that whole failure mode, and this type is public so a
    /// consuming application can apply its own policy on top.
    /// </remarks>
    public sealed class TransientApiException : Exception
    {
        public TransientApiException(string message) : base(message)
        {
        }

        public TransientApiException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Which request/response shape to speak.
    /// </summary>
    /// <remarks>
    /// Domestic Chinese gateways (Zhipu/BigModel, DeepSeek, Moonshot, DashScope,
    /// SiliconFlow) implement Chat Completions almost universally and
    /// <c>/responses</c> almost never. <see cref="Auto"/> exists so the common case
    /// needs no configuration at all -- see <c>ChatClient</c> for how the probe is
    /// cached so it costs at most one wasted request per base URL, ever.
    /// </remarks>
    public enum ApiType
    {
        /// <summary>Try Responses first, fall back to ChatCompletions on 404.</summary>
        Auto,

        /// <summary>The OpenAI Responses API shape (<c>POST /responses</c>).</summary>
        Responses,

        /// <summary>The widely-implemented Chat Completions shape (<c>POST /chat/completions</c>).</summary>
        ChatCompletions
    }
}
