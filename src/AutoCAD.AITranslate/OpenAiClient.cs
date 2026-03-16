using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AutoCAD.AITranslate
{
    internal sealed class OpenAiClient
    {
        private readonly string _apiKey;
        private readonly string _model;
        private readonly string _baseUrl;
        private readonly string _organization;
        private readonly string _project;
        private readonly string _apiType;
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
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim().TrimEnd('/');
            _organization = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim();
            _project = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
            _apiType = string.IsNullOrWhiteSpace(apiType) ? null : apiType.Trim();
            _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim();
            _timeoutMs = timeoutMs.HasValue && timeoutMs.Value > 0 ? timeoutMs.Value : 30000;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public string TranslateToEnglish(string inputText)
        {
            var apiType = NormalizeApiType(_apiType);
            switch (apiType)
            {
                case ApiType.ChatCompletions:
                    return SendChatCompletion(inputText);
                case ApiType.Responses:
                    return SendResponses(inputText);
                case ApiType.Auto:
                default:
                    try
                    {
                        return SendResponses(inputText);
                    }
                    catch (ApiNotFoundException)
                    {
                        return SendChatCompletion(inputText);
                    }
            }
        }

        private string SendResponses(string inputText)
        {
            var requestBody = new
            {
                model = _model,
                temperature = 0.2,
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

            return SendRequest($"{_baseUrl}/responses", requestBody, ExtractOutputText);
        }

        private string SendChatCompletion(string inputText)
        {
            var messages = BuildChatMessages(inputText);
            var requestBody = new
            {
                model = _model,
                temperature = 0.2,
                messages,
                stream = false
            };

            var url = $"{_baseUrl}/chat/completions";
            try
            {
                return SendRequest(url, requestBody, ExtractChatText);
            }
            catch (ApiNotFoundException)
            {
                var fallbackBase = TrimV1Suffix(_baseUrl);
                if (!string.Equals(fallbackBase, _baseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    var fallbackUrl = $"{fallbackBase}/chat/completions";
                    return SendRequest(fallbackUrl, requestBody, ExtractChatText);
                }

                throw;
            }
        }

        private string SendRequest(string url, object requestBody, Func<string, string> extract)
        {
            var json = JsonSerializer.Serialize(requestBody);
            var request = new HttpRequestMessage(HttpMethod.Post, url);
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

            using (var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(_timeoutMs) })
            {
                try
                {
                    var response = client.SendAsync(request).GetAwaiter().GetResult();
                    var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            throw new ApiNotFoundException($"API endpoint not found: {url}. {content}");
                        }

                        throw new InvalidOperationException($"OpenAI API error: {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
                    }

                    return extract(content);
                }
                catch (TaskCanceledException ex)
                {
                    throw new TimeoutException($"OpenAI API timeout after {_timeoutMs} ms: {url}", ex);
                }
            }
        }

        private static string ExtractOutputText(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

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

                        var type = typeElement.GetString();
                        if (!string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase))
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
                                builder.Append("\n");
                            }

                            builder.Append(text.Trim());
                        }
                    }
                }

                return builder.ToString().Trim();
            }

            return string.Empty;
        }

        private static string ExtractChatText(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString()?.Trim() ?? string.Empty;
            }

            if (first.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                return text.GetString()?.Trim() ?? string.Empty;
            }

            return string.Empty;
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

        private static ApiType NormalizeApiType(string apiType)
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

        private enum ApiType
        {
            Auto,
            Responses,
            ChatCompletions
        }

        private sealed class ApiNotFoundException : Exception
        {
            public ApiNotFoundException(string message) : base(message)
            {
            }
        }
    }
}
