using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;

namespace csConversationAgent;

internal class Program
{
    private const string Turn1 = "我叫 Vulcan，我養了三隻鵝，分別叫大白、小白和阿花。";
    private const string Turn2 = "我養了幾隻鵝？牠們叫什麼名字？";

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

        AIAgent agent = new ChatClientAgent(
            chatClient,                                    // 聊天用戶端
            "你是一位友善的助理，請用繁體中文簡短回答。",  // 系統提示詞 instructions
            "助理",                                        // 代理名稱   name
            null);                                         // 代理描述   description

        Console.WriteLine($"{DateTime.Now} 使用的模型: {model}");

        await RunWithoutSessionAsync(agent);   // 對照組：會失憶
        await RunWithSessionAsync(agent);      // 正解：記得
    }

    // ────────────────────────────────────────────────────────────────
    // 執行 1：不傳 session —— 代理會失憶
    // ────────────────────────────────────────────────────────────────
    private static async Task RunWithoutSessionAsync(AIAgent agent)
    {
        Header("執行 1 / 不傳 session —— 代理會失憶");

        // RunAsync 的 session 參數留空時，框架會「每次都幫你開一個新的 session」。
        // 所以下面兩次呼叫其實是兩段毫不相干的對話，第二輪當然答不出來。
        // csFirstAgent 就是這樣寫的，這也是它沒有記憶的原因。
        Say("你", Turn1);
        var answer1 = await agent.RunAsync(Turn1);
        Say("助理", answer1.Text);

        Say("你", Turn2);
        var answer2 = await agent.RunAsync(Turn2);
        Say("助理", answer2.Text);
    }

    // ────────────────────────────────────────────────────────────────
    // 執行 2：兩輪共用同一個 session —— 代理記得
    // ────────────────────────────────────────────────────────────────
    private static async Task RunWithSessionAsync(AIAgent agent)
    {
        Header("執行 2 / 共用同一個 session —— 代理記得");

        // 關鍵就這一行：建立一個 session，兩輪共用。
        // 每次 RunAsync 都會把這一輪的問與答寫回 session，所以下一輪看得到前面說過的話。
        // 注意 CreateSessionAsync 只有非同步版本，要 await。
        var session = await agent.CreateSessionAsync();

        Say("你", Turn1);
        var answer1 = await agent.RunAsync(Turn1, session);
        Say("助理", answer1.Text);

        Say("你", Turn2);
        var answer2 = await agent.RunAsync(Turn2, session);
        Say("助理", answer2.Text);

        // 記憶到底存在哪裡？把 session 裡的歷史掏出來看。
        // 這個歷史是放在記憶體的，程式結束就沒了；要跨行程接續同一段對話，
        // 得改用 session.Serialize() 與 agent.DeserializeSessionAsync()。
        Console.WriteLine();
        Console.WriteLine("【session 裡實際存放的對話歷史】");

        if (session.TryGetInMemoryChatHistory(out var history) && history is not null)
        {
            foreach (var message in history)
            {
                Say(message.Role.ToString(), message.Text);
            }
        }
        else
        {
            Console.WriteLine("  這個 session 沒有把歷史存在記憶體（例如改由服務端保存時）。");
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 輸出小工具
    // ────────────────────────────────────────────────────────────────
    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 74));
    }

    private static void Say(string who, string? text)
    {
        Console.WriteLine();
        Console.WriteLine($"  {who} ▸ {text}");
    }
}
