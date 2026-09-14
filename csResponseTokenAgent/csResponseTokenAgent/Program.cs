using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Diagnostics;
using System.Text;

namespace csResponseTokenAgent;

internal class Program
{
    // 非串流與串流刻意用「同一個」提示詞，兩次的 token 數字才有比較基礎。
    // 注意：輸入 token 會完全一致，輸出 token 仍會因為每次生成內容不同而有落差。
    private const string Prompt = "寫一首關於鵝的長詩，至少八段，每段四行。";

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

        // 價格表放在 appsettings.json，改單價不必重新編譯
        var pricing = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build()
            .GetSection("Pricing")
            .Get<Pricing>() ?? new Pricing();

        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };

        IChatClient chatClient =
            new ChatClient(
                    model,
                    new ApiKeyCredential(apiKey),
                    options)
                .AsIChatClient();

        AIAgent agent = new ChatClientAgent(
            chatClient,                      // 聊天用戶端
            "詩人",                          // 代理名稱
            "創作引人入勝、富有創意的詩。",  // 系統提示詞
            null);                           // 其他設定

        Console.WriteLine($"{DateTime.Now} 使用的模型: {model}");

        await RunNonStreamingAsync(agent, pricing);
        await RunStreamingAsync(agent, pricing);
    }

    // ────────────────────────────────────────────────────────────────
    // 執行 1：非串流
    // ────────────────────────────────────────────────────────────────
    private static async Task RunNonStreamingAsync(AIAgent agent, Pricing pricing)
    {
        Header("執行 1 / 非串流 (RunAsync)");

        var stopwatch = Stopwatch.StartNew();
        AgentResponse response = await agent.RunAsync(Prompt);
        stopwatch.Stop();

        Console.WriteLine(response.Text);
        Console.WriteLine();

        // AgentResponse 只是外殼。AsChatResponse() 把它攤平成 Microsoft.Extensions.AI 的 ChatResponse，
        // 而 ChatResponse.RawRepresentation 就是 OpenAI SDK 原生的 ChatCompletion 物件 ——
        // token 明細（快取命中、推論）只有原生層才看得到，所以一路往下取到這裡。
        var chatResponse = response.AsChatResponse();
        var completion = AsCompletion(chatResponse.RawRepresentation) ?? AsCompletion(response.RawRepresentation);

        Section("回應中繼資料");
        Meta("ModelId", chatResponse.ModelId);
        Meta("ResponseId", response.ResponseId);
        Meta("AgentId", response.AgentId);
        Meta("CreatedAt", response.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Meta("FinishReason", response.FinishReason?.ToString());
        Meta("原生 FinishReason", completion?.FinishReason.ToString());
        Meta("訊息則數", response.Messages.Count.ToString());
        Meta("總耗時", $"{stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        PrintAdditionalProperties(response.AdditionalProperties);

        PrintUsageTable(completion?.Usage);
        PrintCost(completion?.Usage, pricing);
    }

    // ────────────────────────────────────────────────────────────────
    // 執行 2：串流
    // ────────────────────────────────────────────────────────────────
    private static async Task RunStreamingAsync(AIAgent agent, Pricing pricing)
    {
        Header("執行 2 / 串流 (RunStreamingAsync)");

        var stopwatch = Stopwatch.StartNew();
        TimeSpan? timeToFirstToken = null;
        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in agent.RunStreamingAsync(Prompt))
        {
            updates.Add(update);

            if (string.IsNullOrEmpty(update.Text))
            {
                continue;   // 部分更新只帶中繼資料、沒有文字內容
            }

            timeToFirstToken ??= stopwatch.Elapsed;
            Console.Write(update.Text);
        }

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine();

        // 把所有 chunk 聚合成一份完整回應，才拿得到 ResponseId / CreatedAt / FinishReason
        var aggregated = updates.ToAgentResponse();

        // usage 只會出現在其中一個 chunk（通常是最後一個）的 RawRepresentation 上
        ChatTokenUsage? nativeUsage = null;
        foreach (var update in updates)
        {
            var streamingUpdate = AsStreamingCompletion(update.RawRepresentation);
            if (streamingUpdate?.Usage is not null)
            {
                nativeUsage = streamingUpdate.Usage;
            }
        }

        Section("回應中繼資料");
        Meta("ResponseId", aggregated.ResponseId);
        Meta("AgentId", aggregated.AgentId);
        Meta("CreatedAt", aggregated.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Meta("FinishReason", aggregated.FinishReason?.ToString());
        Meta("收到片段 chunk 數", updates.Count.ToString("N0"));
        Meta("首字延遲", $"{timeToFirstToken?.TotalMilliseconds ?? 0:F0} ms");
        Meta("總耗時", $"{stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        PrintUsageTable(nativeUsage);

        // 串流拿不到 usage 是常態，不是錯誤 —— 把原因印出來，這本身就是這個練習的重點之一。
        if (nativeUsage is null)
        {
            Console.WriteLine();
            Console.WriteLine("  ※ 本次串流未回傳 usage。說明：");
            Console.WriteLine("     1. 串流模式下，token 統計不會隨每個 chunk 回傳，只會出現在最後一個 chunk。");
            Console.WriteLine("     2. 伺服器要送出那個 chunk，請求必須帶 stream_options.include_usage = true。");
            Console.WriteLine("     3. OpenAI SDK 2.13.0 的 ChatCompletionOptions.StreamOptions 型別未公開，");
            Console.WriteLine("        無法從外部設定；是否自動帶上取決於 SDK 版本與端點（Azure 部分版本不支援）。");
            Console.WriteLine("     4. 需要精準計費時，改用非串流呼叫。");
        }

        PrintCost(nativeUsage, pricing);
    }

    // ────────────────────────────────────────────────────────────────
    // RawRepresentation 拆包：可能被包了一層 Microsoft.Extensions.AI 的型別，逐層往下找
    // ────────────────────────────────────────────────────────────────
    private static ChatCompletion? AsCompletion(object? raw) => raw switch
    {
        ChatCompletion completion => completion,
        ChatResponse chatResponse => AsCompletion(chatResponse.RawRepresentation),
        AgentResponse agentResponse => AsCompletion(agentResponse.RawRepresentation),
        _ => null
    };

    private static StreamingChatCompletionUpdate? AsStreamingCompletion(object? raw) => raw switch
    {
        StreamingChatCompletionUpdate streamingUpdate => streamingUpdate,
        ChatResponseUpdate chatUpdate => AsStreamingCompletion(chatUpdate.RawRepresentation),
        AgentResponseUpdate agentUpdate => AsStreamingCompletion(agentUpdate.RawRepresentation),
        _ => null
    };

    // ────────────────────────────────────────────────────────────────
    // Token 用量：取自 OpenAI 原生 SDK 的 ChatTokenUsage
    // ────────────────────────────────────────────────────────────────
    private const int LabelWidth = 34;
    private const int ValueWidth = 22;

    private static void PrintUsageTable(ChatTokenUsage? native)
    {
        Section("Token 用量（OpenAI 原生 SDK）");

        Console.WriteLine("  " + PadR("欄位", LabelWidth) + PadL("Token 數", ValueWidth));
        Console.WriteLine("  " + new string('-', LabelWidth + ValueWidth));

        UsageRow("輸入 Input", native?.InputTokenCount);
        UsageRow("  ├─ 快取命中 Cached", native?.InputTokenDetails?.CachedTokenCount);
        UsageRow("  └─ 音訊 Audio", native?.InputTokenDetails?.AudioTokenCount);
        UsageRow("輸出 Output", native?.OutputTokenCount);
        UsageRow("  ├─ 推論 Reasoning", native?.OutputTokenDetails?.ReasoningTokenCount);
        UsageRow("  ├─ 音訊 Audio", native?.OutputTokenDetails?.AudioTokenCount);
        // Predicted Outputs 的兩個欄位在 OpenAI 2.13.0 還標記為實驗性 (OPENAI001)，
        // 預設會被當成錯誤擋下來。這裡只是唯讀顯示，所以就地抑制診斷。
#pragma warning disable OPENAI001
        UsageRow("  ├─ 預測命中 Accepted", native?.OutputTokenDetails?.AcceptedPredictionTokenCount);
        UsageRow("  └─ 預測落空 Rejected", native?.OutputTokenDetails?.RejectedPredictionTokenCount);
#pragma warning restore OPENAI001
        UsageRow("總計 Total", native?.TotalTokenCount);
    }

    private static void UsageRow(string label, long? value)
        => Console.WriteLine("  " + PadR(label, LabelWidth) + PadL(Num(value), ValueWidth));

    private static string Num(long? value) => value?.ToString("N0") ?? "N/A";

    // ────────────────────────────────────────────────────────────────
    // 費用估算
    // ────────────────────────────────────────────────────────────────
    private static void PrintCost(ChatTokenUsage? native, Pricing pricing)
    {
        Section("費用估算");

        if (pricing.InputPerMillion == 0m && pricing.CachedInputPerMillion == 0m && pricing.OutputPerMillion == 0m)
        {
            Console.WriteLine("  尚未設定單價。請編輯 appsettings.json 的 Pricing 區段，");
            Console.WriteLine("  填入每 1,000,000 tokens 的實際單價後重新執行。");
            return;
        }

        // 沒有 usage 就不要硬算 —— 否則三個數字全是 0，會印出一張看起來很正常的零元帳單
        if (native is null)
        {
            Console.WriteLine("  取不到 usage，無法估算本次費用。");
            return;
        }

        long input = native.InputTokenCount;
        long output = native.OutputTokenCount;

        // CachedTokenCount 是 InputTokenCount 的「子集」：已經算在輸入裡面了。
        // 所以要先從輸入扣掉，再用較便宜的快取單價分開計價，不能直接加上去。
        long cached = native.InputTokenDetails?.CachedTokenCount ?? 0;
        long freshInput = Math.Max(0, input - cached);

        // ReasoningTokenCount 同樣是 OutputTokenCount 的「子集」，已含在輸出計價內，
        // 這裡刻意不參與計算 —— 重複計算是最常見的估費錯誤。
        // 單價是美金，但金額一律換算成台幣呈現；匯率同樣放在 appsettings.json，隨時可改
        var rate = pricing.ExchangeRate;
        var inputCost = freshInput / 1_000_000m * pricing.InputPerMillion;
        var cachedCost = cached / 1_000_000m * pricing.CachedInputPerMillion;
        var outputCost = output / 1_000_000m * pricing.OutputPerMillion;
        var total = inputCost + cachedCost + outputCost;

        Console.WriteLine($"  匯率：1 {pricing.Currency} = {pricing.DisplayCurrency} {rate:N2}");
        Console.WriteLine($"  未快取輸入  {freshInput,10:N0} tokens x {pricing.InputPerMillion,8:N4} {pricing.Currency}/M = {pricing.DisplayCurrency} {inputCost * rate,12:F6}");
        Console.WriteLine($"  快取輸入    {cached,10:N0} tokens x {pricing.CachedInputPerMillion,8:N4} {pricing.Currency}/M = {pricing.DisplayCurrency} {cachedCost * rate,12:F6}");
        Console.WriteLine($"  輸出        {output,10:N0} tokens x {pricing.OutputPerMillion,8:N4} {pricing.Currency}/M = {pricing.DisplayCurrency} {outputCost * rate,12:F6}");
        Console.WriteLine($"  合計                                             = {pricing.DisplayCurrency} {total * rate,12:F6}   （{pricing.Currency} {total:F6}）");
        Console.WriteLine($"  換算：同樣用量跑 1,000 次約 {pricing.DisplayCurrency} {total * rate * 1000:N2}");
    }

    // ────────────────────────────────────────────────────────────────
    // 輸出小工具
    // ────────────────────────────────────────────────────────────────
    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine("[36m" + new string('=', 74) + "[0m");
        Console.WriteLine("[36m  " + title + "[0m");
        Console.WriteLine("[36m" + new string('=', 74) + "[0m");
        Console.WriteLine();
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("[33m【" + title + "】[0m");
    }

    private static void Meta(string label, string? value)
    {
        Console.WriteLine("  " + PadR(label, 24) + ": " + (string.IsNullOrWhiteSpace(value) ? "N/A" : value));
    }

    private static void PrintAdditionalProperties(IDictionary<string, object?>? properties)
    {
        if (properties is not { Count: > 0 })
        {
            return;
        }

        foreach (var entry in properties)
        {
            Meta("  " + entry.Key, entry.Value?.ToString());
        }
    }

    // 終端機的中日韓字元佔兩格寬，用 char 數補空白會跑掉，所以自己算顯示寬度
    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var isWide =
                (value >= 0x1100 && value <= 0x115F) ||
                (value >= 0x2E80 && value <= 0xA4CF && value != 0x303F) ||
                (value >= 0xAC00 && value <= 0xD7A3) ||
                (value >= 0xF900 && value <= 0xFAFF) ||
                (value >= 0xFE30 && value <= 0xFE6F) ||
                (value >= 0xFF00 && value <= 0xFF60) ||
                (value >= 0xFFE0 && value <= 0xFFE6);
            width += isWide ? 2 : 1;
        }

        return width;
    }

    private static string PadR(string text, int width) => text + new string(' ', Math.Max(0, width - DisplayWidth(text)));

    private static string PadL(string text, int width) => new string(' ', Math.Max(0, width - DisplayWidth(text))) + text;
}

/// <summary>每 1,000,000 tokens 的單價，由 appsettings.json 的 Pricing 區段綁定。</summary>
internal sealed class Pricing
{
    /// <summary>單價本身的幣別。</summary>
    public string Currency { get; set; } = "USD";

    public decimal InputPerMillion { get; set; }

    public decimal CachedInputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }

    /// <summary>金額最後要用哪個幣別呈現。</summary>
    public string DisplayCurrency { get; set; } = "NT$";

    /// <summary>1 單位的 <see cref="Currency"/> 換算成 <see cref="DisplayCurrency"/> 的匯率。</summary>
    public decimal ExchangeRate { get; set; } = 1m;
}
