using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Diagnostics;

namespace csStreamAgent;

internal class Program
{
    static async Task Main(string[] args)
    {
        var apiKey = Environment.GetEnvironmentVariable("AzureOpenAI_Key");
        var endpoint = Environment.GetEnvironmentVariable("AzureOpenAI_Endpoint");
        var model = "gpt-5.6-luna";

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(endpoint))
        {
            Console.WriteLine("請先設定環境變數 AzureOpenAI_Key 與 AzureOpenAI_Endpoint 後再執行。");
            return;
        }

        IChatClient chatClient =
            new ChatClient(
                    model,
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .AsIChatClient();

        AIAgent agent = new ChatClientAgent(
            chatClient,                      // 聊天用戶端
            "詩人",                          // 代理名稱
            "創作引人入勝、富有創意的詩。.",  // 系統提示詞
            null);                           // 其他設定

        Console.WriteLine($"{DateTime.Now} 開始呼叫 LLM API (串流) / 使用的模型: {model}");

        var stopwatch = Stopwatch.StartNew();
        TimeSpan? timeToFirstToken = null;   // 首字延遲：送出請求到收到第一段文字
        var chunkCount = 0;                  // 總共收到幾個更新片段

        // RunStreamingAsync 回傳 IAsyncEnumerable<AgentRunResponseUpdate>，
        // 模型每產生一小段文字就回傳一次，不必等整段完成
        await foreach (var update in agent.RunStreamingAsync("寫一首關於鵝的長詩，至少八段，每段四行。"))
        {
            chunkCount++;

            if (string.IsNullOrEmpty(update.Text))
            {
                continue;   // 部分更新只帶中繼資料、沒有文字內容
            }

            timeToFirstToken ??= stopwatch.Elapsed;
            Console.Write(update.Text);   // 用 Write 不換行，讓文字接續湧出
        }

        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine($"{DateTime.Now} 完成呼叫 LLM API (串流) / 使用的模型: {model}");
        Console.WriteLine(
            $"首字延遲: {timeToFirstToken?.TotalMilliseconds ?? 0:F0} ms / " +
            $"總耗時: {stopwatch.Elapsed.TotalMilliseconds:F0} ms / " +
            $"收到片段 chunk 數: {chunkCount}");
    }
}
