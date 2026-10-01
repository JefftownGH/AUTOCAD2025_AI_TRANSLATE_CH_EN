using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LlmToolkit
{
    /// <summary>
    /// Helpers for the "ask the model for a JSON array of strings" pattern.
    /// </summary>
    /// <remarks>
    /// Batching several items into one request is the single biggest cost and latency
    /// win available to a bulk caller, but it forces the reply to be machine-parseable.
    /// Models comply imperfectly: they wrap the array in markdown fences, prefix it with
    /// "Sure, here you go", or occasionally drop an element. These helpers absorb that,
    /// and -- critically -- return null rather than a partial result when the shape is
    /// wrong, so a caller never silently applies a misaligned answer to the wrong item.
    /// </remarks>
    public static class JsonArrayProtocol
    {
        /// <summary>
        /// Builds the instruction for a batch request.
        /// </summary>
        /// <param name="inputs">The values to be transformed.</param>
        /// <param name="taskInstruction">
        /// What to do with each element, phrased as a single sentence fragment beginning
        /// with a verb -- for example <c>"Translate each element into English"</c>.
        /// </param>
        /// <param name="preserveVerbatim">
        /// Tokens that must survive unchanged, such as markup codes. Emitted as an
        /// explicit numbered rule for each entry. Empty to skip the rule.
        /// </param>
        public static string BuildBatchPrompt(
            string[] inputs,
            string taskInstruction,
            IEnumerable<string> preserveVerbatim = null)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            var rule = 1;
            var sb = new System.Text.StringBuilder();

            sb.Append(taskInstruction.Trim().TrimEnd('.')).Append(".\n");
            sb.Append("Rules:\n");
            sb.Append($"{rule++}. Keep the array length identical to the input.\n");
            sb.Append($"{rule++}. Keep the same order; element i of the output must be the result for element i of the input.\n");
            sb.Append($"{rule++}. Preserve numbers, units, punctuation and line breaks exactly.\n");

            if (preserveVerbatim != null)
            {
                foreach (var token in preserveVerbatim)
                {
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    sb.Append($"{rule++}. Preserve {token} unchanged.\n");
                }
            }

            sb.Append($"{rule++}. An element that needs no change must be returned exactly as it was.\n");
            sb.Append($"{rule++}. Reply with ONLY a JSON array of strings. No markdown fences, no commentary.\n\n");
            sb.Append("Input:\n");
            sb.Append(JsonSerializer.Serialize(inputs));
            sb.Append("\n\nOutput:");
            return sb.ToString();
        }

        /// <summary>
        /// Parses a JSON array of strings, requiring an exact length match.
        /// </summary>
        /// <returns>
        /// The parsed array, or null when the reply was not a well-formed array of the
        /// expected length. A null result means "fall back to per-item requests", never
        /// "apply what we got".
        /// </returns>
        public static string[] TryParse(string raw, int expectedLength)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var json = ExtractOutermostArray(raw);
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

                var length = doc.RootElement.GetArrayLength();

                // A length mismatch is the dangerous case: the model silently merged or
                // dropped an entry, so index i no longer corresponds to input i. Applying
                // it would write the wrong text onto the wrong drawing entity. Refuse.
                if (length != expectedLength)
                {
                    return null;
                }

                var result = new string[length];
                var i = 0;
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    result[i++] = element.GetString();
                }

                return result;
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
        public static string ExtractOutermostArray(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var start = raw.IndexOf('[');
            var end = raw.LastIndexOf(']');
            if (start < 0 || end <= start)
            {
                return null;
            }

            return raw.Substring(start, end - start + 1);
        }
    }
}
