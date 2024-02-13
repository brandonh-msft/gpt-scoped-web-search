using System.Text;
using System.Text.RegularExpressions;

using Azure;

using ChatBot;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Plugins.Web;
using Microsoft.SemanticKernel.Plugins.Web.Bing;

using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

#pragma warning disable SKEXP0054 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

IKernelBuilder kb = Kernel.CreateBuilder()
                .AddAzureOpenAIChatCompletion(Environment.GetEnvironmentVariable("OpenAI::DeploymentName")!,
                                              Environment.GetEnvironmentVariable("OpenAI::EndpointURL")!,
                                              Environment.GetEnvironmentVariable("OpenAI::ApiKey")!);

kb.Services
    .AddHttpClient()
    .AddLogging(b =>
        b.AddSimpleConsole()
    //.SetMinimumLevel(LogLevel.Trace)
    );

Kernel k = kb.Build();
k.ImportPluginFromObject(new WebSearchEnginePlugin(new ScopedBingConnector(Environment.GetEnvironmentVariable("Bing::ApiKey")!, k.LoggerFactory)), "Bing");
k.ImportPluginFromType<Helpers>();

using var tracer = Sdk.CreateTracerProviderBuilder()
    .AddHttpClientInstrumentation(o =>
    {
        o.FilterHttpRequestMessage = (request) => request.RequestUri?.ToString().Contains("openai", StringComparison.OrdinalIgnoreCase) is true;
        o.EnrichWithHttpRequestMessage = (activity, request) => k.LoggerFactory.CreateLogger(nameof(o.EnrichWithHttpWebRequest)).LogInformation("Request Body: {requestBody}", request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "null");
        o.EnrichWithHttpResponseMessage = (activity, response) => k.LoggerFactory.CreateLogger(nameof(o.EnrichWithHttpResponseMessage)).LogInformation("Response Body: {responseBody}", response.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "null");
    })
    .Build();

var settings = new OpenAIPromptExecutionSettings
{
    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
    Temperature = 0.2,
    PresencePenalty = -2,
    FrequencyPenalty = 1.0
};

var prompt = @"You are an AI assistant helping users find current information about the El Niño weather phenomenon. Your answers must be complete, accurate, and relevant to the user's question. Reply only about El Niño and, if using any external sources, do not reply until you have fully analyzed all the data you collected and have an answer for the user. Use URL Encoding for non-alphanumeric characters in the user's question or your requests to tools and functions. Use the Functions available to you to search the web, download pages, and request their contents.

Here is the user's question:

{{$input}}";

var retryHeaderRegex = new Regex(@"RETRY-AFTER: (\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

ILogger log = k.LoggerFactory.CreateLogger("ChatBot");

do
{
    Console.Write("What would you like to know about El Niño? ");
    var search = Console.ReadLine()!;

    while (true)
    {
        try
        {
            var debugOutput = new StringBuilder();

            await foreach (StreamingKernelContent s in k.InvokePromptStreamingAsync(prompt, new(settings) { ["input"] = search }))
            {
                Console.Write(s);
                debugOutput.Append(s);
            }

            if (debugOutput.Length > 0)
            {
                log.LogDebug(debugOutput.ToString());
            }
            else
            {
                log.LogError("No response received.");
                Console.Error.WriteLine("I appear to have encountered an error processing your question. Please do try again.");
            }

            break;
        }
        catch (HttpOperationException e) when (e.StatusCode is System.Net.HttpStatusCode.TooManyRequests && e.InnerException is RequestFailedException rfe)
        {
            // Parse the inner exception's message and find the `RETRY-AFTER: ##` line
            if (retryHeaderRegex.Match(rfe.Message) is Match match && match.Success)
            {
                var retryAfter = int.Parse(match.Groups[1].Value);
                log.LogWarning($"Rate limited. Retrying after {retryAfter} seconds.");
                await Task.Delay(TimeSpan.FromSeconds(retryAfter));
            }
            else
            {
                log.LogError(e, "Rate limited without a retry timeframe. Aborting.");
                break;
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "An error occurred. Aborting.");
            break;
        }
    }

    Console.WriteLine();
    Console.WriteLine();
} while (true);

/// <summary>
/// Retrieves search results from the Bing search engine using the provided API key.
/// </summary>
class ScopedBingConnector : IWebSearchEngineConnector
{
    private readonly BingConnector _bing;
    private readonly ILogger<ScopedBingConnector> _log;

    public ScopedBingConnector(string apiKey, ILoggerFactory loggerFactory)
    {
        _bing = new(apiKey, loggerFactory);
        _log = loggerFactory.CreateLogger<ScopedBingConnector>();
    }

    /// <summary>
    /// Searches for a specified query asynchronously and returns a collection of strings.
    /// </summary>
    /// <param name="query">The query string to search for.</param>
    /// <param name="count">The number of results to retrieve. Default is 1.</param>
    /// <param name="offset">The offset to start retrieving results from. Default is 0.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation. Default is the default cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation and returns a collection of strings.</returns>
    public async Task<IEnumerable<string>> SearchAsync(string query, int count = 1, int offset = 0, CancellationToken cancellationToken = default)
    {
        _log.LogDebug(@"Querying with (""El Niño"" OR ""El Nino"" OR ""La Niña"" OR ""La Nina"") (site:wmo.int OR site:noaa.gov)");
        var results = await _bing.SearchAsync($@"{query} (""El Niño"" OR ""El Nino"" OR ""La Niña"" OR ""La Nina"") (site:wmo.int OR site:noaa.gov)", count, offset, cancellationToken);
        if (!results.Any())
        {
            _log.LogWarning("No results returned.");

            _log.LogDebug(@"Querying with (""El Niño"" OR ""El Nino"" OR ""La Niña"" OR ""La Nina"")");
            results = await _bing.SearchAsync($@"{query} (""El Niño"" OR ""El Nino"" OR ""La Niña"" OR ""La Nina"")", count, offset, cancellationToken);
        }

        if (!results.Any())
        {
            _log.LogWarning("No results returned.");

            _log.LogDebug(@"Querying with (site:wmo.int OR site:noaa.gov)");
            results = await _bing.SearchAsync($@"{query} (site:wmo.int OR site:noaa.gov)", count, offset, cancellationToken);
        }

        if (!results.Any())
        {
            _log.LogWarning("No results returned. Querying with no scoping");
            results = await _bing.SearchAsync(query, count, offset, cancellationToken);
        }

        _log.LogTrace("{numResults} results returned.", results.Count());
        return results;
    }
}

#pragma warning restore SKEXP0054 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.