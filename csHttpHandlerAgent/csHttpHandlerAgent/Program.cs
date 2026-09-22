using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace csHttpHandlerAgent;

internal class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var apiKey = Environment.GetEnvironmentVariable("AzureOpenAI_Key");
        var endpoint = Environment.GetEnvironmentVariable("AzureOpenAI_Endpoint");
        var model = "gpt-5.6-luna";

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(endpoint))
        {
            Console.WriteLine("請先設定環境變數 AzureOpenAI_Key 與 AzureOpenAI_Endpoint 後再執行。");
            return;
        }

        using var httpClient = new HttpClient(new LoggingHttpHandler(new HttpClientHandler()));

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            Transport = new HttpClientPipelineTransport(httpClient)
        };

        IChatClient chatClient =
            new ChatClient(
                    model,
                    new ApiKeyCredential(apiKey),
                    options)
                .AsIChatClient();

        AIAgent agent = new ChatClientAgent(
            chatClient,                      // 聊天用戶端
            "創作引人入勝、富有創意的詩。",  // 系統提示詞 instructions
            "詩人",                          // 代理名稱   name
            null);                           // 代理描述   description

        Console.WriteLine($"{DateTime.Now} 開始呼叫 LLM API / 使用的模型: {model}");

        var response = await agent.RunAsync("寫一個關於鵝的詩。");

        Console.WriteLine();
        Console.WriteLine(response.Text);
        Console.WriteLine($"{DateTime.Now} 完成呼叫 LLM API / 使用的模型: {model}");
    }
}

internal sealed class LoggingHttpHandler : DelegatingHandler
{
    private static readonly string[] SecretHeaders =
        ["api-key", "Authorization", "OpenAI-Organization", "OpenAI-Project"];

    public LoggingHttpHandler(HttpMessageHandler innerHandler) : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine(new string('─', 74));
        Console.WriteLine($"→ 送出  {request.Method} {request.RequestUri}");
        DumpHeaders(request.Headers);

        if (request.Content is not null)
        {
            DumpHeaders(request.Content.Headers);
            Console.WriteLine($"  {await request.Content.ReadAsStringAsync(cancellationToken)}");
        }

        var stopwatch = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine($"← 收到  {(int)response.StatusCode} {response.ReasonPhrase}   耗時 {stopwatch.ElapsedMilliseconds} ms");
        DumpHeaders(response.Headers);
        DumpHeaders(response.Content.Headers);

        await response.Content.LoadIntoBufferAsync(cancellationToken);
        Console.WriteLine($"  {await response.Content.ReadAsStringAsync(cancellationToken)}");
        Console.WriteLine(new string('─', 74));

        return response;
    }

    private static void DumpHeaders(HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            var value = SecretHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)
                ? "***"
                : string.Join(", ", header.Value);

            Console.WriteLine($"  {header.Key}: {value}");
        }
    }
}
