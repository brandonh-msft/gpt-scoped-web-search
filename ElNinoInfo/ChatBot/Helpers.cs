namespace ChatBot
{
    using System;
    using System.ComponentModel;

    using Microsoft.Extensions.Logging;
    using Microsoft.SemanticKernel;

    internal class Helpers(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
    {
        private readonly ILogger _log = loggerFactory.CreateLogger<Helpers>();

        [KernelFunction]
        [Description("Gets the current Date & Time in RFC 1123 format")]
        public string GetCurrentDateTime()
        {
            using var scope = _log.BeginScope(nameof(GetCurrentDateTime));
            var retVal = DateTimeOffset.UtcNow.ToString("R");
            _log.LogTrace(retVal);

            return retVal;
        }

        [KernelFunction]
        [Description("Gets the content of a URL.")]
        public async Task<string> GetUrlContentAsync(Uri url)
        {
            using var scope = _log.BeginScope(nameof(GetUrlContentAsync));

            var client = httpClientFactory.CreateClient(nameof(GetUrlContentAsync));
            client.Timeout = TimeSpan.FromSeconds(5);   // This needs to be fast as part of the chatbot!
            try
            {
                return await client.GetStringAsync(url);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                throw new Exception($@"'{url}' was not found.");
            }
        }
    }
}
