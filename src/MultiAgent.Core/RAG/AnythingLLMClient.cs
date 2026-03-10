using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace MultiAgent.Core.RAG;

public interface IAnythingLLMClient
{
    Task<string> QueryWorkspaceAsync(string workspace, string question, CancellationToken ct = default);
    Task IngestTextAsync(string workspace, string content, string docName, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

public sealed class AnythingLLMClient(
    HttpClient http,
    string baseUrl,
    string apiKey,
    ILogger<AnythingLLMClient> logger) : IAnythingLLMClient
{
    public async Task<string> QueryWorkspaceAsync(
        string workspace,
        string question,
        CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{baseUrl}/api/v1/workspace/{workspace}/chat");

            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = JsonContent.Create(new
            {
                message = question,
                mode = "query" // modo apenas de recuperação, não gera resposta longa
            });

            var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("AnythingLLM retornou {Status} para workspace {WS}",
                    response.StatusCode, workspace);
                return string.Empty;
            }

            var result = await response.Content
                .ReadFromJsonAsync<AnythingLLMResponse>(ct);

            return result?.TextResponse ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao consultar AnythingLLM workspace [{WS}]", workspace);
            return string.Empty;
        }
    }

    public async Task IngestTextAsync(
        string workspace,
        string content,
        string docName,
        CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{baseUrl}/api/v1/workspace/{workspace}/upload-text");

            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = JsonContent.Create(new
            {
                textContent = content,
                metadata = new { title = docName }
            });

            var response = await http.SendAsync(request, ct);
            logger.LogInformation("Ingestão em [{WS}]: {Status}", workspace, response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao ingerir no AnythingLLM");
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/auth");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private sealed record AnythingLLMResponse(string TextResponse, List<Source>? Sources);
    private sealed record Source(string Title, string? Url);
}

/// <summary>
/// Cliente nulo para quando AnythingLLM não está disponível.
/// Os agentes continuam funcionando sem RAG.
/// </summary>
public sealed class NullAnythingLLMClient : IAnythingLLMClient
{
    public Task<string> QueryWorkspaceAsync(string workspace, string question, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    public Task IngestTextAsync(string workspace, string content, string docName, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(false);
}
