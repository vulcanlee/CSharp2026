#pragma warning disable OPENAI001 // OpenAI SDK 2.14 的 ResponsesClient 仍標記為實驗性 API

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using System.ClientModel;
using System.Text;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage; // OpenAI.Chat 也有 ChatMessage，這裡指定用 MEAI 的

namespace csResponsesAgent;

internal class Program
{
    private const string Instructions = "你是邏輯老師，請用繁體中文作答，先給結論再用三句話內說明理由。";

    // 同一道邏輯題：三種情境都問它，數字才有比較基礎
    // 刻意選一題「需要想一下」的題目：推論太短（約 200 token 以內）時，服務端常常不回推論摘要
    private const string Puzzle =
        "有四個盒子，標籤分別是「蘋果」「橘子」「香蕉」「混合」，但四個標籤全部貼錯了。" +
        "你只能從其中一個盒子拿出一顆水果來看（不能看盒子裡面），" +
        "能不能保證推出四個盒子的正確內容？請說明。";

    // 第二輪追問：故意不重述規則，只有記得上一輪的代理才答得出來
    private const string FollowUp = "那如果只有三個盒子（蘋果、橘子、混合），規則都一樣，應該從哪個盒子拿？";

    // 由聊天用戶端中介層記錄：這一輪「實際送出」幾則訊息、帶出去的對話 id 是什麼
    private static int lastSentMessages;
    private static string? lastSentConversationId;

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

        // ── Responses API 代理：ResponsesClient + AsAIAgent()（Microsoft.Agents.AI.OpenAI 提供的擴充方法）
        AIAgent responsesAgent =
            new ResponsesClient(
                    new ApiKeyCredential(apiKey),
                    new ResponsesClientOptions { Endpoint = new Uri(endpoint) })
                .AsAIAgent(
                    model: model,
                    instructions: Instructions,
                    name: "邏輯老師（Responses）",
                    clientFactory: client => client.AsBuilder().Use(CountMessages, null).Build());

        // ── Chat Completions 代理：就是 MFA001～MFA003 用的 ChatClient，只是改用同一個套件的 AsAIAgent() 建立
        AIAgent chatAgent =
            new ChatClient(
                    model,
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .AsAIAgent(
                    instructions: Instructions,
                    name: "邏輯老師（Chat Completions）",
                    clientFactory: client => client.AsBuilder().Use(CountMessages, null).Build());

        Console.WriteLine($"使用的模型：{model}");
        Console.WriteLine($"邏輯題：{Puzzle}");

        // ── 示範一：設定 reasoning effort 與推論摘要，同一道題比較 Low 與 High
        Header("示範一：Responses API ＋ 推論摘要（同一道題，effort = Low vs High）");

        foreach (var effort in new[] { ReasoningEffort.Low, ReasoningEffort.High })
        {
            Section($"reasoning effort = {effort}");
            AgentResponse response = await responsesAgent.RunAsync(Puzzle, options: ReasoningRunOptions(effort));
            PrintReasoningAndAnswer(response);
            PrintUsage(response.Usage);
        }

        // ── 示範二：服務端保存對話（Responses API）
        Header("示範二：服務端保存對話（Responses API，只傳上一輪的 response id）");
        var responsesTurns = await RunTwoTurnsAsync(responsesAgent);

        // ── 示範三：本機保存對話（Chat Completions，MFA003 的作法）
        Header("示範三：對照組 Chat Completions（每一輪都把整段歷史重送）");
        var chatTurns = await RunTwoTurnsAsync(chatAgent);

        // ── 對照小結：只看第二輪（追問那一輪）
        Header("對照小結：第二輪（追問）");
        Row("實際送出的訊息數", $"{responsesTurns[1].SentMessages} 則", $"{chatTurns[1].SentMessages} 則");
        Row("帶出的對話 id", responsesTurns[1].SentConversationId is null ? "（無）" : "上一輪的 response id", chatTurns[1].SentConversationId ?? "（無）");
        Row("輸入 token（其中快取）", $"{responsesTurns[1].InputTokens}（{responsesTurns[1].CachedTokens}）", $"{chatTurns[1].InputTokens}（{chatTurns[1].CachedTokens}）");
        Row("推論 token", $"{responsesTurns[1].ReasoningTokens}", $"{chatTurns[1].ReasoningTokens}");
        Row("兩輪中拿到推論摘要", responsesTurns.Any(t => t.HasSummary) ? "是" : "否", chatTurns.Any(t => t.HasSummary) ? "是" : "否");
        Row("對話歷史存在哪裡", "OpenAI 服務端", "本機 AgentSession");

        // 這是最容易誤會的地方：「不重送歷史」省的是傳輸量和自己管理歷史的工作，不是 token
        Console.WriteLine();
        Console.WriteLine("  觀察：Responses 第二輪只送 1 則訊息，但服務端會把上一輪的完整內容（含推論項目）接回上下文，");
        Console.WriteLine("        所以輸入 token 照樣計費，甚至可能比 Chat Completions 多；Chat Completions 重送的只有看得見的文字，");
        Console.WriteLine("        上一輪的推論內容直接丟掉。（提示快取要前綴超過約 1,024 token 才會命中，這個範例太短所以是 0）");
    }

    // 兩種 API 都用 MEAI 的 ChatOptions.Reasoning 設定：Effort 是推論力道，Output = Summary 是要求回傳推論摘要
    private static ChatClientAgentRunOptions ReasoningRunOptions(ReasoningEffort effort) =>
        new(new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = effort, Output = ReasoningOutput.Summary }
        });

    private static async Task<List<TurnStat>> RunTwoTurnsAsync(AIAgent agent)
    {
        AgentSession session = await agent.CreateSessionAsync();
        var turns = new List<TurnStat>();

        foreach (var (title, question) in new[] { ("第一輪：原題", Puzzle), ("第二輪：追問（不重述題目）", FollowUp) })
        {
            Section(title);
            AgentResponse response = await agent.RunAsync(question, session, ReasoningRunOptions(ReasoningEffort.Low));
            PrintReasoningAndAnswer(response);

            // Responses API 存在服務端時，ConversationId 就是這一輪的 response id；Chat Completions 則一直是 null
            var conversationId = (session as ChatClientAgentSession)?.ConversationId;
            Console.WriteLine($"  實際送出訊息數：{lastSentMessages} 則");
            Console.WriteLine($"  帶出的對話 id ：{lastSentConversationId ?? "（無）"}");
            Console.WriteLine($"  回來後 session.ConversationId：{conversationId ?? "（無，歷史存在本機 session）"}");
            PrintUsage(response.Usage);

            turns.Add(new TurnStat(
                lastSentMessages,
                lastSentConversationId,
                response.Usage?.InputTokenCount ?? 0,
                response.Usage?.CachedInputTokenCount ?? 0,
                response.Usage?.ReasoningTokenCount ?? 0,
                GetReasoningSummary(response).Length > 0));
        }

        return turns;
    }

    // 聊天用戶端中介層：記下這一輪真正送到 API 的訊息數，以及帶出去的對話 id（Responses 的 previous_response_id）
    private static Task<ChatResponse> CountMessages(
        IEnumerable<ChatMessage> messages, ChatOptions? options, IChatClient innerClient, CancellationToken cancellationToken)
    {
        var list = messages.ToList();
        lastSentMessages = list.Count;
        lastSentConversationId = options?.ConversationId;
        return innerClient.GetResponseAsync(list, options, cancellationToken);
    }

    // 推論摘要放在回應訊息裡的 TextReasoningContent；答案則是一般的 TextContent（response.Text）
    private static string GetReasoningSummary(AgentResponse response) =>
        string.Join(Environment.NewLine, response.Messages
            .SelectMany(message => message.Contents)
            .OfType<TextReasoningContent>()
            .Select(reasoning => reasoning.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static void PrintReasoningAndAnswer(AgentResponse response)
    {
        var summary = GetReasoningSummary(response);

        Console.WriteLine("  [推論摘要]");
        Console.WriteLine(summary.Length > 0
            ? Indent(summary)
            : "    （沒有收到推論摘要：Chat Completions 不提供；Responses 則在推論太短時會省略）");
        Console.WriteLine("  [答案]");
        Console.WriteLine(Indent(response.Text));
    }

    private static void PrintUsage(UsageDetails? usage)
    {
        Console.WriteLine(
            $"  token：輸入 {usage?.InputTokenCount}（快取 {usage?.CachedInputTokenCount ?? 0}）" +
            $"／輸出 {usage?.OutputTokenCount}（其中推論 {usage?.ReasoningTokenCount ?? 0}）" +
            $"／總計 {usage?.TotalTokenCount}");
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(line => "    " + line.TrimEnd('\r')));

    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 70));
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"【{title}】");
    }

    private static void Row(string label, string responses, string chat) =>
        Console.WriteLine($"  {label}：Responses = {responses}｜Chat Completions = {chat}");
}

internal sealed record TurnStat(
    int SentMessages,
    string? SentConversationId,
    long InputTokens,
    long CachedTokens,
    long ReasoningTokens,
    bool HasSummary);
