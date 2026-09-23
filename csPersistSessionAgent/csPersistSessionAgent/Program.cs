using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace csPersistSessionAgent;

internal class Program
{
    private const string StoryStart = "請開始說一個關於一隻叫小熊的睡前故事，只講開頭一小段（約 100 字），講到一個懸念就停下來，先不要講完。";
    private const string StoryResume = "我們昨天講到哪裡了？請先用一句話提醒我，然後接著往下講一小段。";

    private const int PreviewLines = 8;   // JSON 節錄要印幾行

    // 存檔位置：bin 輸出目錄旁邊的 session.json
    private static readonly string SessionPath = Path.Combine(AppContext.BaseDirectory, "session.json");

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
            chatClient,  // 聊天用戶端
            "你是一位溫柔的睡前故事說書人，請用繁體中文，每次只講一小段（約 100 字），語氣輕柔適合小朋友入睡。",  // 系統提示詞 instructions
            "說書人",  // 代理名稱   name
            null); // 代理描述   description

        Console.WriteLine($"{DateTime.Now} 使用的模型: {model}");

        if (File.Exists(SessionPath))
        {
            await TomorrowAsync(agent);   // 明天：載入存檔，故事接著講
        }
        else
        {
            await TonightAsync(agent);    // 今晚：開一個新故事，講到一半就存檔
        }
    }

    private static async Task TonightAsync(AIAgent agent)
    {
        Header("今晚 / 找不到 session.json —— 開一個新故事");

        var session = await agent.CreateSessionAsync();

        Say("你", StoryStart);
        var answer = await agent.RunAsync(StoryStart, session);
        Say("說書人", answer.Text);

        await SaveSessionAsync(agent, session);

        Console.WriteLine();
        Console.WriteLine("👉 現在關掉程式，再執行一次 dotnet run，故事會從這裡接下去。");
    }

    private static async Task TomorrowAsync(AIAgent agent)
    {
        Header("明天 / 找到 session.json —— 故事接著講");

        Console.WriteLine();
        Console.WriteLine("── 對照組：全新 session（沒有昨晚的記憶）──");

        var freshSession = await agent.CreateSessionAsync();

        Say("你", StoryResume);
        var forgotten = await agent.RunAsync(StoryResume, freshSession);
        Say("說書人", forgotten.Text);

        Console.WriteLine();
        Console.WriteLine("---------------------------------------------------");
        Console.WriteLine();
        Console.WriteLine("── 正解：載入 session.json 還原昨晚的 session ──");

        var session = await LoadSessionAsync(agent);

        Say("你", StoryResume);
        var continued = await agent.RunAsync(StoryResume, session);
        Say("說書人", continued.Text);

        await ChatUntilBedtimeAsync(agent, session);
        await SaveSessionAsync(agent, session);

        Console.WriteLine();
        Console.WriteLine("👉 想重新開始一個新故事，把 session.json 刪掉即可。");
    }

    private static async Task ChatUntilBedtimeAsync(AIAgent agent, AgentSession session)
    {
        Console.WriteLine();
        Console.WriteLine("（想繼續聊就輸入一句話，直接按 Enter 結束並存檔）");

        while (true)
        {
            Console.Write("  你 ▸ ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
            {
                break;
            }

            var answer = await agent.RunAsync(input, session);
            Say("說書人", answer.Text);
        }
    }

    private static async Task SaveSessionAsync(AIAgent agent, AgentSession session)
    {
        JsonElement state = await agent.SerializeSessionAsync(session);
        await File.WriteAllTextAsync(SessionPath, JsonSerializer.Serialize(state, JsonWriteOptions));

        ShowSavedFile();
    }

    private static async Task<AgentSession> LoadSessionAsync(AIAgent agent)
    {
        var json = await File.ReadAllTextAsync(SessionPath);

        using var document = JsonDocument.Parse(json);
        return await agent.DeserializeSessionAsync(document.RootElement);
    }

    private static void ShowSavedFile()
    {
        var bytes = new FileInfo(SessionPath).Length;

        Console.WriteLine();
        Console.WriteLine($"💾 已存檔：{SessionPath}（{bytes:N0} bytes）");

        foreach (var line in File.ReadLines(SessionPath).Take(PreviewLines))
        {
            Console.WriteLine($"  │ {(line.Length > 70 ? line[..70] + "…" : line)}");
        }

        Console.WriteLine("  │ …（略）");
    }

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
