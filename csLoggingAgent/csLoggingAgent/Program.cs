using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;

namespace csLoggingAgent;

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

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss.fff ");
            builder.SetMinimumLevel(LogLevel.Trace);
        });

        IChatClient chatClient =
            new ChatClient(
                    model,
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .AsIChatClient();

        AIAgent agent = new ChatClientAgent(
            chatClient,                      // 聊天用戶端
            "詩人",                          // 代理名稱
            "創作引人入勝、富有創意的詩。",  // 系統提示詞
            null);                           // 其他設定

        AIAgent loggedAgent = agent
            .AsBuilder()
            .UseLogging(loggerFactory)
            .Build();

        Console.WriteLine($"{DateTime.Now} 開始呼叫 LLM API / 使用的模型: {model}");

        var response = await loggedAgent.RunAsync("寫一個關於鵝的詩。");

        Console.WriteLine();
        Console.WriteLine(response.Text);
        Console.WriteLine($"{DateTime.Now} 完成呼叫 LLM API / 使用的模型: {model}");
    }
}
