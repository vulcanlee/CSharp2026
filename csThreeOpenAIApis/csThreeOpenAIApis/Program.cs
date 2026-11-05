using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace csThreeOpenAIApis;

// ═══════════════════════════════════════════════════════════════════════════════════════
//  OpenAI 的三代文字生成 API，用 HttpClient 直接打，看清楚它們的差別
//
//  /v1/completions       （2020，GPT-3 時代）輸入是一段「純文字」，模型只負責「接著寫下去」。
//                         沒有角色、沒有對話、沒有工具、沒有結構化輸出，全部要靠提示詞自己約定。
//  /v1/chat/completions  （2023，ChatGPT 時代）輸入是 messages 陣列（system / user / assistant / tool），
//                         無狀態：每一輪都要把整段歷史重送；已成為業界「OpenAI 相容 API」的共同標準。
//  /v1/responses         （2025，代理時代）輸入是 instructions + input 項目，輸出是 output[] 項目清單。
//                         可以把對話存在服務端（store + previous_response_id），工具呼叫與推論都是一等公民。
//
//  同一個「線上商店客服助理」情境，依序跑四個示範，每次呼叫都印出實際送出的請求 JSON 與回應 JSON：
//    示範一：單輪問答        → 請求／回應的基本形狀
//    示範二：多輪對話        → 對話狀態由誰保管
//    示範三：結構化輸出      → 輸出 JSON 有沒有保證
//    示範四：工具呼叫（完整迴圈）→ 模型要求呼叫函式、本機執行、把結果送回去
//  最後印出對照表與「什麼情況選哪一種」的建議。
// ═══════════════════════════════════════════════════════════════════════════════════════

internal class Program
{
    // ── 情境資料 ────────────────────────────────────────────────────────────────────────

    private const string StorePolicy =
        "你是「橘子 3C」線上商店的客服助理，請用繁體中文、口語、精簡（三句話內）回答顧客。\n" +
        "店家規定：\n" +
        "- 到貨後享 7 天鑑賞期，商品與包裝完整即可無條件退貨，退款 5 個工作天內退回原付款方式。\n" +
        "- 單筆滿 NT$1,000 免運，未滿酌收 NT$80 運費。\n" +
        "- 商品若有瑕疵，保固期內免費維修或換新。\n" +
        "目前販售的降噪藍牙耳機（依序為第一、二、三款）：\n" +
        "1. AirQuiet Lite：NT$1,990，顏色白／藍，保固 1 年，輕巧適合通勤。\n" +
        "2. SoundShield Pro：NT$2,890，顏色黑／銀，保固 2 年，降噪最強、續航 30 小時。\n" +
        "3. NoiseZero Max：NT$4,590，顏色黑，保固 2 年，耳罩式、音質最好。";

    // 假的訂單資料庫：示範四的工具會查這裡
    private static readonly Dictionary<string, Order> Orders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A1001"] = new("A1001", "SoundShield Pro（黑）", "已出貨", "黑貓宅急便", "9012-3456-7788", "11/7（四）送達"),
        ["A1002"] = new("A1002", "AirQuiet Lite（藍）", "備貨中", null, null, "預計 11/8（五）出貨"),
    };

    // ── 連線設定 ────────────────────────────────────────────────────────────────────────

    private static HttpClient api = null!;             // 打 /chat/completions 與 /responses
    private static HttpClient completionsApi = null!;  // 打 /completions（可另外指向本機伺服器）
    private static string model = "gpt-6.1-sol";
    private static string completionsModel = "gpt-6.1-sol";
    private static bool completionsAvailable = true;   // 第一次失敗後改成 false，之後只印請求不送出

    // 請求 JSON：中文不跳脫、縮排、null 欄位不輸出（例如第一輪沒有 previous_response_id）
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // 把模型回傳的工單 JSON（snake_case）反序列化成 C# record
    private static readonly JsonSerializerOptions JsonIn = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // Azure 會在回應裡附上大量內容過濾結果，跟三種 API 的差異無關，印出前先拿掉
    private static readonly HashSet<string> AzureOnlyFields =
        ["content_filter_results", "content_filters", "prompt_filter_results", "routing"];

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var apiKey = Environment.GetEnvironmentVariable("AzureOpenAI_Key");
        var endpoint = Environment.GetEnvironmentVariable("AzureOpenAI_Endpoint"); // 例如 https://xxx.services.ai.azure.com/openai/v1/

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(endpoint))
        {
            Console.WriteLine("請先設定環境變數 AzureOpenAI_Key 與 AzureOpenAI_Endpoint 後再執行。");
            return;
        }

        // /v1/completions 是舊 API：新模型（包含 gpt-6.1-sol）都不支援，Azure 上能用的補全模型也都已退場。
        // 想看到它真正跑起來，可以另外設定這三個環境變數，改接支援 /v1/completions 的伺服器，例如：
        //   Ollama ：Completions_Endpoint=http://localhost:11434/v1/   Completions_Model=qwen2.5:7b   Completions_Key=ollama
        //   vLLM   ：Completions_Endpoint=http://localhost:8000/v1/
        var completionsEndpoint = Environment.GetEnvironmentVariable("Completions_Endpoint") ?? endpoint;
        var completionsKey = Environment.GetEnvironmentVariable("Completions_Key") ?? apiKey;
        completionsModel = Environment.GetEnvironmentVariable("Completions_Model") ?? model;

        api = CreateClient(endpoint, apiKey);
        completionsApi = CreateClient(completionsEndpoint, completionsKey);

        Console.WriteLine($"Chat Completions／Responses：{model} @ {MaskHost(endpoint)}");
        Console.WriteLine($"Completions               ：{completionsModel} @ {MaskHost(completionsEndpoint)}");

        await Demo1SingleTurnAsync();
        await Demo2MultiTurnAsync();
        await Demo3StructuredOutputAsync();
        await Demo4ToolCallingAsync();
        PrintComparisonAndAdvice();
    }

    // 三種 API 共用同一種驗證方式：OpenAI 標準的 Authorization: Bearer（Azure 的 /openai/v1/ 端點也接受）
    private static HttpClient CreateClient(string endpoint, string key)
    {
        var client = new HttpClient
        {
            // 結尾一定要有 /，後面用相對路徑 "chat/completions" 才會接成 .../openai/v1/chat/completions
            BaseAddress = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/"),
            Timeout = TimeSpan.FromMinutes(5), // 推論模型想久一點也不會逾時
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════
    //  示範一：單輪問答 —— 同一個問題，三種請求長什麼樣子
    // ═══════════════════════════════════════════════════════════════════════════════════
    private static async Task Demo1SingleTurnAsync()
    {
        const string question = "耳機買了不喜歡可以退嗎？幾天內要退？";
        Header($"示範一：單輪問答（{question}）");

        // ── /v1/completions：只有一個 prompt 字串
        // 沒有「角色」：店家規定、顧客的話、客服的開頭，全部是我們自己用文字排出來的「劇本」。
        // 模型看到結尾的「客服：」就會接著寫；stop 告訴伺服器看到「\n顧客：」就停，
        // 否則模型很可能自己替顧客編下一句、一路自問自答下去。
        Section("/v1/completions");
        var completions = await PostCompletionsAsync(new
        {
            model = completionsModel,
            prompt = $"{StorePolicy}\n\n顧客：{question}\n客服：",
            max_tokens = 300,          // completions 的上限參數叫 max_tokens
            stop = new[] { "\n顧客：" },
        });
        PrintAnswer(completions, CompletionsText);
        PrintUsage(completions, ApiKind.Completions);

        // ── /v1/chat/completions：messages 陣列，每則訊息有 role
        // system 放規則、user 放顧客的話，伺服器會依模型的聊天格式自動組好，不必自己排劇本、也不用 stop。
        Section("/v1/chat/completions");
        var chat = await PostAsync(api, "chat/completions", new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = StorePolicy },
                new { role = "user", content = question },
            },
            max_completion_tokens = 2000, // 推論模型改用這個名稱，而且推論 token 也算在裡面，所以要給寬一點
        });
        PrintAnswer(chat, ChatText);
        PrintUsage(chat, ApiKind.Chat);

        // ── /v1/responses：instructions + input
        // 規則放 instructions（只對這一次請求有效），顧客的話放 input（可以是字串，也可以是項目陣列）。
        // 回應不是 choices，而是 output[] 項目清單：可能有 reasoning、message、function_call 等不同型別。
        Section("/v1/responses");
        var responses = await PostAsync(api, "responses", new
        {
            model,
            instructions = StorePolicy,
            input = question,
            max_output_tokens = 2000, // responses 的上限參數又換了一個名字
            store = false,            // 單輪問答不需要服務端保存（預設是 true）
        });
        PrintAnswer(responses, ResponsesText);
        PrintUsage(responses, ApiKind.Responses);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════
    //  示範二：多輪對話 —— 「上一輪說了什麼」由誰記住
    // ═══════════════════════════════════════════════════════════════════════════════════
    private static async Task Demo2MultiTurnAsync()
    {
        Header("示範二：多輪對話（第二輪的「第二款」要靠上一輪才看得懂）");

        string[] turns =
        [
            "我想買降噪藍牙耳機，預算 3000 元以內，有推薦嗎？",
            "那第二款有黑色嗎？保固多久？",
        ];
        var stats = new List<TurnStat>();

        // ── /v1/completions：自己維護逐字稿字串，每一輪把「整段」送出去
        // 歷史怎麼排版、多長要截斷、哪裡是誰說的話，全部是應用程式的責任。
        Section("/v1/completions（自己拼逐字稿）");
        var transcript = new StringBuilder(StorePolicy).Append('\n');
        for (var i = 0; i < turns.Length; i++)
        {
            transcript.Append($"\n顧客：{turns[i]}\n客服：");
            Console.WriteLine($"\n  ▶ 第 {i + 1} 輪：{turns[i]}");
            var result = await PostCompletionsAsync(new
            {
                model = completionsModel,
                prompt = transcript.ToString(),
                max_tokens = 300,
                stop = new[] { "\n顧客：" },
            });
            var answer = result.Ok ? CompletionsText(result.Json!).Trim() : "（沒有回應，先用占位文字讓逐字稿繼續）";
            transcript.Append(answer); // 模型的回答也要自己接回逐字稿，下一輪才「記得」
            PrintAnswer(result, CompletionsText);
            stats.Add(new("completions", i + 1, $"1 個字串（含 {i + 1} 輪對話）", result.RequestBytes,
                result.Ok ? Usage(result.Json!, ApiKind.Completions).Input : null));
        }

        // ── /v1/chat/completions：自己維護 messages 陣列，每一輪「整個陣列」重送
        // 比拼字串好：角色清楚、伺服器幫你套聊天格式；但仍然是無狀態，歷史越長請求越大。
        Section("/v1/chat/completions（每輪重送整個 messages）");
        var messages = new List<object> { new { role = "system", content = StorePolicy } };
        for (var i = 0; i < turns.Length; i++)
        {
            messages.Add(new { role = "user", content = turns[i] });
            Console.WriteLine($"\n  ▶ 第 {i + 1} 輪：{turns[i]}");
            var result = await PostAsync(api, "chat/completions", new
            {
                model,
                messages,
                max_completion_tokens = 2000,
            });
            PrintAnswer(result, ChatText);
            stats.Add(new("chat/completions", i + 1, $"{messages.Count} 則 messages", result.RequestBytes,
                result.Ok ? Usage(result.Json!, ApiKind.Chat).Input : null));
            if (result.Ok)
            {
                messages.Add(new { role = "assistant", content = ChatText(result.Json!) }); // 一樣要自己接回去
            }
        }

        // ── /v1/responses：store = true，下一輪只送「新的那一句」+ previous_response_id
        // 歷史存在 OpenAI／Azure 服務端，應用程式只要記住上一輪的 response id。
        // ⚠ 注意：instructions 不會跟著 previous_response_id 延續！每一輪都要重送，否則第二輪就沒有店家規定。
        Section("/v1/responses（服務端保存，只傳 previous_response_id）");
        string? previousResponseId = null;
        for (var i = 0; i < turns.Length; i++)
        {
            Console.WriteLine($"\n  ▶ 第 {i + 1} 輪：{turns[i]}");
            var result = await PostAsync(api, "responses", new
            {
                model,
                instructions = StorePolicy,
                input = turns[i],
                previous_response_id = previousResponseId, // 第一輪是 null，序列化時會被省略
                store = true,
                max_output_tokens = 2000,
            });
            PrintAnswer(result, ResponsesText);
            stats.Add(new("responses", i + 1, previousResponseId is null ? "1 句" : "1 句 + previous_response_id",
                result.RequestBytes, result.Ok ? Usage(result.Json!, ApiKind.Responses).Input : null));
            previousResponseId = result.Json?["id"]?.GetValue<string>();
        }

        Section("對照：每一輪實際送出了什麼");
        foreach (var s in stats)
        {
            Console.WriteLine($"  {s.Api,-17} 第 {s.Turn} 輪｜送出：{s.Sent}｜請求 {s.Bytes,5:N0} bytes｜輸入 token：{s.InputTokens?.ToString() ?? "—"}");
        }
        Console.WriteLine();
        Console.WriteLine("  觀察：responses 第二輪的請求最小，但輸入 token 並沒有變少——服務端會把上一輪的完整內容接回上下文，");
        Console.WriteLine("        照樣計費。它省下的是「傳輸量」和「自己保管歷史的程式碼」，不是錢。");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════
    //  示範三：結構化輸出 —— 把顧客抱怨轉成客服工單 JSON
    // ═══════════════════════════════════════════════════════════════════════════════════
    private static async Task Demo3StructuredOutputAsync()
    {
        const string complaint =
            "我上週買的耳機，訂單 A1001，右耳完全沒聲音，才用三天！我要退錢，你們再不處理我就去消保會投訴。";
        Header("示範三：結構化輸出（顧客抱怨 → 客服工單 JSON）");
        Console.WriteLine($"  顧客抱怨：{complaint}");

        // ── /v1/completions：沒有 response_format，只能在提示詞裡「拜託」模型輸出 JSON
        // 模型可能加上 ```json 圍欄、前後多一句說明、漏欄位、或分類寫成清單以外的字——沒有任何保證，
        // 所以解析一定要 try/catch，還得自己檢查 enum 值，失敗了只能重試。
        Section("/v1/completions（提示詞要求 JSON，自己解析）");
        var completions = await PostCompletionsAsync(new
        {
            model = completionsModel,
            prompt =
                "請把顧客抱怨轉成客服工單，只輸出一個 JSON 物件，不要任何說明文字。\n" +
                "欄位：category（商品瑕疵／物流問題／退換貨／帳務問題／其他）、priority（低／中／高）、" +
                "order_id（訂單編號，沒有就填 null）、summary（20 字內摘要）、needs_refund（true/false）。\n\n" +
                $"顧客抱怨：{complaint}\n工單 JSON：",
            max_tokens = 300,
        });
        if (completions.Ok)
        {
            var text = CompletionsText(completions.Json!);
            Console.WriteLine($"  [模型原始輸出]\n{Indent(text)}");
            TryParseTicketFromFreeText(text);
        }

        // ── /v1/chat/completions：response_format = json_schema，strict 模式由伺服器保證輸出符合 schema
        Section("/v1/chat/completions（response_format.json_schema）");
        var chat = await PostAsync(api, "chat/completions", new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = "你是客服工單分類員，請把顧客抱怨轉成客服工單。summary 請在 20 字內。" },
                new { role = "user", content = complaint },
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = "support_ticket", strict = true, schema = TicketSchema }, // 多包一層 json_schema
            },
            max_completion_tokens = 2000,
        });
        if (chat.Ok)
        {
            // strict 模式下模型若拒答，content 會是 null，原因放在 refusal
            var refusal = chat.Json!["choices"]?[0]?["message"]?["refusal"]?.GetValue<string>();
            if (refusal is not null) Console.WriteLine($"  模型拒答：{refusal}");
            else PrintTicket(ChatText(chat.Json!));
        }

        // ── /v1/responses：text.format = json_schema（name 與 schema 直接放在 format 底下，少一層）
        Section("/v1/responses（text.format）");
        var responses = await PostAsync(api, "responses", new
        {
            model,
            instructions = "你是客服工單分類員，請把顧客抱怨轉成客服工單。summary 請在 20 字內。",
            input = complaint,
            text = new
            {
                format = new { type = "json_schema", name = "support_ticket", strict = true, schema = TicketSchema },
            },
            store = false,
            max_output_tokens = 2000,
        });
        if (responses.Ok)
        {
            // 輸出被 max_output_tokens 截斷時 status 會是 incomplete，JSON 可能不完整
            var status = responses.Json!["status"]?.GetValue<string>();
            if (status != "completed") Console.WriteLine($"  回應狀態：{status}（JSON 可能不完整）");
            PrintTicket(ResponsesText(responses.Json!));
        }
    }

    // 工單的 JSON Schema：chat 與 responses 共用同一份
    // strict 模式的規則：每個欄位都要列在 required、additionalProperties 必須是 false；
    // 「可以沒有」的欄位用 ["string", "null"] 表示，而不是從 required 拿掉。
    private static readonly object TicketSchema = new
    {
        type = "object",
        properties = new
        {
            category = new { type = "string", @enum = new[] { "商品瑕疵", "物流問題", "退換貨", "帳務問題", "其他" } },
            priority = new { type = "string", @enum = new[] { "低", "中", "高" } },
            order_id = new { type = new[] { "string", "null" }, description = "訂單編號，沒有提到就是 null" },
            summary = new { type = "string", description = "20 字內的問題摘要" },
            needs_refund = new { type = "boolean" },
        },
        required = new[] { "category", "priority", "order_id", "summary", "needs_refund" },
        additionalProperties = false,
    };

    // completions 的「自由文字 JSON」：先照原樣解析，失敗再做常見的補救（拿掉圍欄、只取第一個 { 到最後一個 }）
    private static void TryParseTicketFromFreeText(string text)
    {
        try
        {
            PrintTicket(text);
            return;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"  ✗ 原樣解析失敗：{ex.Message}");
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            Console.WriteLine("  ✗ 找不到 JSON 物件，只能重試或改提示詞。");
            return;
        }

        try
        {
            Console.WriteLine("  → 補救：只取第一個 { 到最後一個 } 再解析");
            PrintTicket(text[start..(end + 1)]);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"  ✗ 補救後仍失敗：{ex.Message}");
        }
    }

    private static void PrintTicket(string json)
    {
        var ticket = JsonSerializer.Deserialize<Ticket>(json, JsonIn)
                     ?? throw new JsonException("解析結果是 null");

        // schema 的 enum 只約束 chat／responses 的 strict 模式；completions 沒有這層保護，要自己再驗一次
        string[] categories = ["商品瑕疵", "物流問題", "退換貨", "帳務問題", "其他"];
        string[] priorities = ["低", "中", "高"];
        var valid = categories.Contains(ticket.Category) && priorities.Contains(ticket.Priority);

        Console.WriteLine("  [反序列化成 C# Ticket]");
        Console.WriteLine($"    分類：{ticket.Category}｜優先：{ticket.Priority}｜訂單：{ticket.OrderId ?? "（無）"}｜要退款：{(ticket.NeedsRefund ? "是" : "否")}");
        Console.WriteLine($"    摘要：{ticket.Summary}");
        Console.WriteLine(valid ? "    ✓ 值都在允許清單內" : "    ✗ 分類或優先等級不在允許清單內");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════
    //  示範四：工具呼叫（完整迴圈）—— 模型決定要查訂單，C# 執行，把結果送回去
    // ═══════════════════════════════════════════════════════════════════════════════════
    private static async Task Demo4ToolCallingAsync()
    {
        const string question = "我的訂單 A1001 到哪了？A1002 也幫我看一下。";
        const int maxRounds = 5; // 防止模型一直要求呼叫工具而形成無窮迴圈
        Header($"示範四：工具呼叫完整迴圈（{question}）");

        // ── /v1/completions：沒有 tools 參數，只能在提示詞裡約定一個「呼叫格式」，再用 regex 抓
        // 模型可能寫錯格式、一次寫兩行、或乾脆編一個查詢結果出來——這就是它脆弱的地方。
        Section("/v1/completions（提示詞約定 CALL 格式 + regex）");
        var prompt = new StringBuilder(StorePolicy)
            .Append("\n你可以查詢訂單。需要查詢時，只輸出一行「CALL get_order_status(訂單編號)」，每次一筆，")
            .Append("輸出後就停止，系統會在「查詢結果：」後面提供資料；拿到所有資料後再回答顧客。\n")
            .Append($"\n顧客：{question}\n客服：");
        for (var round = 1; round <= maxRounds; round++)
        {
            Console.WriteLine($"\n  ▶ 第 {round} 圈");
            var result = await PostCompletionsAsync(new
            {
                model = completionsModel,
                prompt = prompt.ToString(),
                max_tokens = 300,
                stop = new[] { "\n顧客：", "\n查詢結果：" }, // 模型想自己編查詢結果時就攔下來
            });
            if (!result.Ok) break;

            var text = CompletionsText(result.Json!).Trim();
            var match = Regex.Match(text, @"CALL\s+get_order_status\(\s*([A-Za-z0-9]+)\s*\)");
            if (!match.Success)
            {
                Console.WriteLine($"  [最終答覆]\n{Indent(text)}");
                break;
            }

            var output = GetOrderStatus(match.Groups[1].Value);
            Console.WriteLine($"  模型要求：get_order_status({match.Groups[1].Value}) → 本機執行結果：{output}");
            prompt.Append($"{text}\n查詢結果：{output}\n客服：");
        }

        // ── /v1/chat/completions：tools + tool_calls + role = "tool"
        // 1. 送出 tools 定義（外面包一層 { type: "function", function: {...} }）
        // 2. finish_reason == "tool_calls" 時，把 assistant 的 tool_calls 原樣接回 messages
        // 3. 每個呼叫各回一則 { role: "tool", tool_call_id, content }，再把整段 messages 重送
        Section("/v1/chat/completions（tools → tool_calls → role=tool）");
        var chatTools = new object[]
        {
            new
            {
                type = "function",
                function = new { name = "get_order_status", description = OrderToolDescription, parameters = OrderToolParameters, strict = true },
            },
        };
        var messages = new List<object>
        {
            new { role = "system", content = StorePolicy },
            new { role = "user", content = question },
        };
        // ⚠ 實測：gpt-6.1-sol 在 chat/completions 上帶 tools 一定回 400——
        //   "Function tools with reasoning_effort are not supported for gpt-6.1-sol in /v1/chat/completions.
        //    To use function tools, use /v1/responses or set reasoning_effort to 'none'."
        // 但它又不接受 reasoning_effort = "none"（只支援 low／medium／high／xhigh），所以在 chat/completions 上沒有解。
        // 新一代推論模型在 chat/completions 上「推論」與「工具」只能二選一，兩個都要就得用 responses。
        // 這裡先用 gpt-6.1-sol 打一次讓你看到錯誤，再改用不開推論的 gpt-5.6-luna 把迴圈跑完。
        var chatToolModel = model;
        for (var round = 1; round <= maxRounds; round++)
        {
            Console.WriteLine($"\n  ▶ 第 {round} 圈（{chatToolModel}，送出 {messages.Count} 則 messages）");
            var result = await PostAsync(api, "chat/completions", new
            {
                model = chatToolModel,
                messages,
                tools = chatTools,
                max_completion_tokens = 2000,
            });
            if (!result.Ok)
            {
                if (chatToolModel == FallbackChatToolModel) break;
                Console.WriteLine($"  ⚠ {chatToolModel} 在 chat/completions 上無法同時使用推論與工具，改用 {FallbackChatToolModel}（不指定 reasoning_effort）重跑這一圈。");
                chatToolModel = FallbackChatToolModel;
                round--;
                continue;
            }

            var choice = result.Json!["choices"]![0]!;
            var toolCalls = choice["message"]?["tool_calls"]?.AsArray();
            if (choice["finish_reason"]?.GetValue<string>() != "tool_calls" || toolCalls is null)
            {
                Console.WriteLine($"  [最終答覆]\n{Indent(ChatText(result.Json!))}");
                break;
            }

            // assistant 這則訊息（含 tool_calls）一定要接回去，否則下一則 role=tool 會找不到對應的呼叫而被拒絕
            messages.Add(new { role = "assistant", tool_calls = toolCalls.DeepClone() });
            foreach (var call in toolCalls)
            {
                var callId = call!["id"]!.GetValue<string>();
                var arguments = call["function"]!["arguments"]!.GetValue<string>(); // 注意：arguments 是「JSON 字串」，不是物件
                var output = RunOrderTool(arguments);
                Console.WriteLine($"  模型要求：get_order_status({arguments})［{callId}］→ 本機執行結果：{output}");
                messages.Add(new { role = "tool", tool_call_id = callId, content = output });
            }
        }

        // ── /v1/responses：tools + function_call 項目 + function_call_output 項目
        // 1. tools 定義是扁平的 { type: "function", name, parameters }，少一層 function
        // 2. 呼叫要求是 output[] 裡 type == "function_call" 的項目（用 call_id 對應）
        // 3. 下一輪 input 只放 function_call_output 項目 + previous_response_id，歷史由服務端接上
        //    （instructions 與 tools 一樣不會延續，要重送）
        Section("/v1/responses（function_call → function_call_output）");
        var responsesTools = new object[]
        {
            new { type = "function", name = "get_order_status", description = OrderToolDescription, parameters = OrderToolParameters, strict = true },
        };
        object input = question;
        string? previousResponseId = null;
        for (var round = 1; round <= maxRounds; round++)
        {
            Console.WriteLine($"\n  ▶ 第 {round} 圈");
            var result = await PostAsync(api, "responses", new
            {
                model,
                instructions = StorePolicy,
                input,
                tools = responsesTools,
                previous_response_id = previousResponseId,
                store = true, // 要用 previous_response_id 接續，上一輪就必須存在服務端
                max_output_tokens = 2000,
            });
            if (!result.Ok) break;

            var functionCalls = result.Json!["output"]!.AsArray()
                .Where(item => item?["type"]?.GetValue<string>() == "function_call")
                .ToList();
            if (functionCalls.Count == 0)
            {
                Console.WriteLine($"  [最終答覆]\n{Indent(ResponsesText(result.Json!))}");
                break;
            }

            var outputs = new List<object>();
            foreach (var call in functionCalls)
            {
                var callId = call!["call_id"]!.GetValue<string>();
                var arguments = call["arguments"]!.GetValue<string>();
                var output = RunOrderTool(arguments);
                Console.WriteLine($"  模型要求：get_order_status({arguments})［{callId}］→ 本機執行結果：{output}");
                outputs.Add(new { type = "function_call_output", call_id = callId, output });
            }
            input = outputs;
            previousResponseId = result.Json["id"]!.GetValue<string>();
        }
    }

    // 示範四 chat/completions 的備援模型：不指定 reasoning_effort 時可以使用 function tools
    private const string FallbackChatToolModel = "gpt-5.6-luna";

    private const string OrderToolDescription = "依訂單編號查詢出貨狀態、物流單號與預計到貨日";

    private static readonly object OrderToolParameters = new
    {
        type = "object",
        properties = new { order_id = new { type = "string", description = "訂單編號，例如 A1001" } },
        required = new[] { "order_id" },
        additionalProperties = false,
    };

    // 模型給的 arguments 是 JSON 字串：{"order_id":"A1001"}
    private static string RunOrderTool(string argumentsJson)
    {
        var orderId = JsonNode.Parse(argumentsJson)?["order_id"]?.GetValue<string>() ?? "";
        return GetOrderStatus(orderId);
    }

    // 真正的「工具」：查假資料庫，回傳 JSON 字串給模型讀
    private static string GetOrderStatus(string orderId) =>
        Orders.TryGetValue(orderId, out var order)
            ? JsonSerializer.Serialize(order, JsonTool)
            : JsonSerializer.Serialize(new { error = $"查無訂單 {orderId}" }, JsonTool);

    // 工具結果：單行、snake_case、中文不跳脫
    private static readonly JsonSerializerOptions JsonTool = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ═══════════════════════════════════════════════════════════════════════════════════
    //  結尾：對照表與決策建議
    // ═══════════════════════════════════════════════════════════════════════════════════
    private static void PrintComparisonAndAdvice()
    {
        Header("對照表");

        (string Topic, string Completions, string Chat, string Responses)[] rows =
        [
            ("輸入形式", "prompt 純文字字串", "messages 陣列", "instructions + input（字串或項目陣列）"),
            ("角色", "沒有，自己用文字排劇本", "system / user / assistant / tool", "instructions + 各種型別的項目"),
            ("回應形式", "choices[].text", "choices[].message", "output[] 項目清單（reasoning／message／function_call…）"),
            ("對話狀態", "自己拼逐字稿", "自己保管 messages，每輪重送", "可存在服務端：store + previous_response_id"),
            ("結構化輸出", "只能靠提示詞，沒有保證", "response_format.json_schema（strict）", "text.format（strict）"),
            ("工具呼叫", "自己約定格式 + regex", "tools → tool_calls → role=tool（推論模型不能並用，gpt-6.1-sol 完全不行）", "tools → function_call → function_call_output（可與推論並用）"),
            ("token 上限參數", "max_tokens", "max_completion_tokens", "max_output_tokens"),
            ("usage 欄位", "prompt_tokens / completion_tokens", "prompt_tokens / completion_tokens", "input_tokens / output_tokens"),
            ("支援的模型", "舊補全模型、base 模型", "幾乎所有聊天模型", "OpenAI／Azure 的新模型"),
            ("跨供應商相容", "部分本機伺服器", "業界共同標準（Ollama、vLLM、各家雲端）", "主要是 OpenAI／Azure，其他家陸續跟進"),
        ];

        foreach (var row in rows)
        {
            Console.WriteLine($"\n  ■ {row.Topic}");
            Console.WriteLine($"    completions      ：{row.Completions}");
            Console.WriteLine($"    chat/completions ：{row.Chat}");
            Console.WriteLine($"    responses        ：{row.Responses}");
        }

        Header("什麼情況選哪一種");
        Console.WriteLine("""
          ● /v1/responses —— 在 OpenAI／Azure 上開新專案的首選
            · 用推論模型：推論內容可以跨輪接續，不會每輪丟掉重想
            · 推論模型 + 工具呼叫：示範四實測 gpt-6.1-sol 在 chat/completions 上兩者不能並用，responses 可以
            · 想把對話狀態交給服務端，或要用內建工具（網頁搜尋、檔案搜尋、程式碼執行、MCP）、背景任務
            · 注意：store 預設會把對話存在服務端；也比較綁定 OpenAI／Azure

          ● /v1/chat/completions —— 需要「到處都能跑」或「自己掌控歷史」時
            · 同一份程式要能切換 Ollama、vLLM、其他雲端的 OpenAI 相容服務
            · 無狀態：歷史完全由自己保管，方便稽核、遮罩個資、不在服務端留資料
            · 既有系統已經用它，而且不需要 responses 獨有的功能

          ● /v1/completions —— 新專案不要用
            · 只剩 base 模型或補全型微調模型、本機推論伺服器上的純文字續寫／填空
            · 需要完全掌控 prompt 模板（例如自己套聊天格式做研究）時才考慮
            · 角色、多輪、JSON、工具都要靠提示詞硬做，示範三、四的 prompt 與解析程式碼就看得出有多脆弱
        """);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════
    //  HTTP 與 JSON 小工具
    // ═══════════════════════════════════════════════════════════════════════════════════

    // 送出一個 POST：先印請求 JSON，再印（拿掉 Azure 專屬欄位的）回應 JSON
    // send = false 時只印請求、不送出（用在 completions 已確認不可用的情況）
    private static async Task<ApiResult> PostAsync(HttpClient client, string path, object body, bool send = true)
    {
        var requestJson = JsonSerializer.Serialize(body, JsonOut);
        var requestBytes = Encoding.UTF8.GetByteCount(requestJson);

        Console.WriteLine($"  ┌─ POST {path}（{requestBytes:N0} bytes）{(send ? "" : "（略過送出）")}");
        Console.WriteLine(Indent(requestJson, "  │ "));

        if (!send)
        {
            Console.WriteLine("  └─");
            return new(null, false, requestBytes);
        }

        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content);
        var responseText = await response.Content.ReadAsStringAsync();

        JsonNode? json = null;
        try { json = JsonNode.Parse(responseText); } catch (JsonException) { /* 錯誤頁面可能不是 JSON */ }

        Console.WriteLine($"  ├─ 回應 {(int)response.StatusCode} {response.StatusCode}（已省略 Azure 專屬的內容過濾欄位）");
        Console.WriteLine(Indent(json is null ? responseText : StripAzureFields(json.DeepClone())!.ToJsonString(JsonOut), "  │ "));
        Console.WriteLine("  └─");

        return new(json, response.IsSuccessStatusCode, requestBytes);
    }

    // completions 專用：第一次失敗就記下來，之後只印請求、不再打 API
    private static async Task<ApiResult> PostCompletionsAsync(object body)
    {
        var result = await PostAsync(completionsApi, "completions", body, send: completionsAvailable);
        if (completionsAvailable && !result.Ok)
        {
            completionsAvailable = false;
            Console.WriteLine($"""
                  ⚠ /v1/completions 呼叫失敗。這是舊版 API，{completionsModel} 這類新模型已不提供，
                    Azure 上支援它的補全模型（gpt-35-turbo-instruct、davinci、babbage）也都已退場。
                    之後的 completions 步驟只印出請求 JSON，讓你看到它要怎麼組，不再實際送出。
                    想看實際結果：設定 Completions_Endpoint／Completions_Model／Completions_Key 改接
                    Ollama、vLLM 等支援 /v1/completions 的本機伺服器。
                """);
        }
        return result;
    }

    private static JsonNode? StripAzureFields(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).Where(AzureOnlyFields.Contains).ToList()) obj.Remove(key);
                foreach (var (_, value) in obj) StripAzureFields(value);
                break;
            case JsonArray array:
                foreach (var item in array) StripAzureFields(item);
                break;
        }
        return node;
    }

    // ── 三種 API 各自「取出答案文字」的方式

    private static string CompletionsText(JsonNode json) =>
        json["choices"]?[0]?["text"]?.GetValue<string>() ?? "";

    private static string ChatText(JsonNode json) =>
        json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";

    // 原始 JSON 沒有 output_text 這個欄位（那是 SDK 幫你算的便利屬性），
    // 要自己從 output[] 裡挑出 type == "message" 的項目，再取其中 type == "output_text" 的文字
    private static string ResponsesText(JsonNode json) =>
        string.Concat(json["output"]?.AsArray()
            .Where(item => item?["type"]?.GetValue<string>() == "message")
            .SelectMany(item => item!["content"]?.AsArray() ?? new JsonArray())
            .Where(part => part?["type"]?.GetValue<string>() == "output_text")
            .Select(part => part!["text"]?.GetValue<string>()) ?? []);

    private static void PrintAnswer(ApiResult result, Func<JsonNode, string> extract)
    {
        if (result.Ok) Console.WriteLine($"  [答案]\n{Indent(extract(result.Json!).Trim())}");
    }

    // ── usage 欄位：completions／chat 叫 prompt_tokens／completion_tokens，responses 改叫 input_tokens／output_tokens

    private enum ApiKind { Completions, Chat, Responses }

    private static (long? Input, long? Output, long? Reasoning) Usage(JsonNode json, ApiKind kind)
    {
        var usage = json["usage"];
        return kind == ApiKind.Responses
            ? (usage?["input_tokens"]?.GetValue<long>(), usage?["output_tokens"]?.GetValue<long>(),
               usage?["output_tokens_details"]?["reasoning_tokens"]?.GetValue<long>())
            : (usage?["prompt_tokens"]?.GetValue<long>(), usage?["completion_tokens"]?.GetValue<long>(),
               usage?["completion_tokens_details"]?["reasoning_tokens"]?.GetValue<long>());
    }

    private static void PrintUsage(ApiResult result, ApiKind kind)
    {
        if (!result.Ok) return;
        var (input, output, reasoning) = Usage(result.Json!, kind);
        var names = kind == ApiKind.Responses ? ("input_tokens", "output_tokens") : ("prompt_tokens", "completion_tokens");
        Console.WriteLine($"  [usage] {names.Item1} = {input}｜{names.Item2} = {output}（其中推論 {reasoning ?? 0}）");
    }

    // ── 主控台排版

    private static string MaskHost(string endpoint) =>
        Regex.Replace(endpoint, @"//[^./]+\.", "//***.");

    private static string Indent(string text, string prefix = "    ") =>
        string.Join(Environment.NewLine, text.Split('\n').Select(line => prefix + line.TrimEnd('\r')));

    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 78));
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"【{title}】");
    }
}

internal sealed record ApiResult(JsonNode? Json, bool Ok, int RequestBytes);

internal sealed record TurnStat(string Api, int Turn, string Sent, int Bytes, long? InputTokens);

internal sealed record Order(string OrderId, string Product, string Status, string? Carrier, string? TrackingNo, string Eta);

internal sealed record Ticket(string Category, string Priority, string? OrderId, string Summary, bool NeedsRefund);
