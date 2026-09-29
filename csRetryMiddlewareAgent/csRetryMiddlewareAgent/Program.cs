using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage; // OpenAI.Chat 也有 ChatMessage，這裡指定用 MEAI 的

namespace csRetryMiddlewareAgent;

internal class Program
{
    // 回答的字數上限（直接用 response.Text.Length 計算，中文一個字算 1，標點與換行也算）
    private const int MaxChars = 100;

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

        IChatClient chatClient =
            new ChatClient(
                    model,
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .AsIChatClient();

        // 1. 原始代理：系統提示詞故意讓它喜歡寫長詩
        AIAgent poet = new ChatClientAgent(
            chatClient,
            "你是一位詩人，創作引人入勝、富有創意的詩，而且特別喜歡寫長詩。", // 系統提示詞
            "詩人");                                                         // 代理名稱

        // 2. 套上自己寫的中介層：只給 runFunc，串流版交給框架用 runFunc 的結果代打
        AIAgent guardedPoet = poet.AsBuilder()
            .Use(runFunc: RetryIfTooLong, runStreamingFunc: null)
            .Build();

        Console.WriteLine($"使用的模型：{model}／字數上限：{MaxChars} 字");

        Console.WriteLine();
        Console.WriteLine("===== 對照組：沒有中介層 =====");
        await AskAsync(poet, "寫一個關於鵝的詩。");

        Console.WriteLine();
        Console.WriteLine("===== 套上中介層 =====");
        await AskAsync(guardedPoet, "寫一個關於鵝的詩。");
    }

    // 業務程式碼：只認得 AIAgent，不知道有沒有中介層（兩次呼叫一模一樣）
    private static async Task AskAsync(AIAgent agent, string question)
    {
        Console.WriteLine($"🧑 {question}");
        var response = await agent.RunAsync(question);
        Console.WriteLine($"🤖（{response.Text.Length} 字）");
        Console.WriteLine(response.Text);
    }

    // 中介層：先讓內層代理回答，超過字數上限就在同一個 session 追問一次
    private static async Task<AgentResponse> RetryIfTooLong(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        // 呼叫端沒給 session 就自己開一個，重問時代理才看得到自己上一個回答
        session ??= await innerAgent.CreateSessionAsync(cancellationToken);

        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);
        if (response.Text.Length <= MaxChars)
        {
            return response;
        }

        Console.WriteLine($"🛡️ 中介層：回答 {response.Text.Length} 字，超過上限 {MaxChars} 字，要求重答");
        var retry = await innerAgent.RunAsync(
            $"你的回答有 {response.Text.Length} 字，超過 {MaxChars} 字的上限。請把同一首詩精簡到 {MaxChars} 字以內重新回答。",
            session, options, cancellationToken);

        if (retry.Text.Length > MaxChars)
        {
            Console.WriteLine($"⚠️ 中介層：重答後仍有 {retry.Text.Length} 字，照樣回傳");
        }
        return retry;
    }
}
