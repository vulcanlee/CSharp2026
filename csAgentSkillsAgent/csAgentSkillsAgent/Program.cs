using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage; // OpenAI.Chat 也有 ChatMessage，這裡指定用 MEAI 的

namespace csAgentSkillsAgent;

internal class Program
{
    // 所有代理共用的基本指示；技能包代理的「技能摘要」會由 AgentSkillsProvider 自動附加在後面
    private const string BaseInstructions = "你是工程團隊的助理，請用繁體中文回答。";

    // 問題 A：跟週報無關。技能包代理應該「只看到摘要、不載入技能」
    private const string QuestionA = "用一句話說明什麼是站立會議（stand-up meeting）？";

    // 問題 B：寫週報。技能包代理應該 load_skill → read_skill_resource（→ run_skill_script）
    private const string QuestionB =
        """
        幫我把這些本週工作筆記整理成週報，姓名：王小明。
        - 修好登入頁在 Safari 白畫面的 bug（已上線）
        - 訂單 API 加了快取，平均回應從 820ms 降到 310ms
        - 報表匯出功能做到一半，大概七成，下週三前應該可以弄完
        - 和 PM 對了會員分級規格，還有兩個欄位沒定案，要等行銷部回覆
        - 測試環境資料庫空間快滿了（剩 8%），需要 DevOps 幫忙擴充
        - 下週要開始做推播通知的技術評估
        - 本週 Jira 排了 8 張單，完成 5 張
        """;

    // 由觀察用中介層累加：目前這一題的統計
    private static QuestionStat? current;

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
                .AsIChatClient()
                // 觀察用中介層：掛在 ChatClientAgent 內建的函式呼叫迴圈「下面」，所以每一次往返 LLM 都看得到
                .AsBuilder().Use(Observe, null).Build();

        var skillsFolder = Path.Combine(AppContext.BaseDirectory, "skills");
        Console.WriteLine($"使用的模型：{model}");
        Console.WriteLine($"File 來源的技能資料夾：{skillsFolder}");

        var stats = new List<QuestionStat>();

        // ── 示範一：File 來源 —— 指定資料夾，AgentSkillsProvider 自己找 */SKILL.md，並把同資料夾的 .md 等檔案當成資源
        Header("示範一：File 來源（skills/weekly-report/SKILL.md ＋ 範本檔）");
        using (var fileProvider = new AgentSkillsProvider(skillsFolder, options: TrustedSkillOptions()))
        {
            stats.AddRange(await RunBothQuestionsAsync("File", CreateAgent(chatClient, "週報助理（File）", fileProvider)));
        }

        // ── 示範二：Inline 來源 —— 在程式碼裡 new AgentInlineSkill，再用 AddResource／AddScript 掛上資源與腳本
        Header("示範二：Inline 來源（AgentInlineSkill，程式碼內建）");
        var inlineSkill = new AgentInlineSkill(
            name: WeeklyReportContent.SkillName,
            description: WeeklyReportContent.Description,
            instructions: WeeklyReportContent.CodeInstructions("Inline"));
        inlineSkill.AddResource("template", WeeklyReportContent.Template, "週報範本（Markdown），必須照這份範本的標題與順序輸出");
        inlineSkill.AddResource("writing-guide", WeeklyReportContent.WritingGuide, "週報用語規範");
        inlineSkill.AddResource("this-week", () => WeeklyReportContent.ThisWeek(), "本週一到週五的日期範圍"); // 委派 = 動態資源
        inlineSkill.AddScript("completion-rate", (int done, int total) => WeeklyReportContent.CompletionRate(done, total),
            "依完成項數與總項數計算本週完成率，並回傳燈號");

        using (var inlineProvider = new AgentSkillsProvider([inlineSkill], TrustedSkillOptions()))
        {
            stats.AddRange(await RunBothQuestionsAsync("Inline", CreateAgent(chatClient, "週報助理（Inline）", inlineProvider)));
        }

        // ── 示範三：Class 來源 —— WeeklyReportSkill : AgentClassSkill<WeeklyReportSkill>，資源與腳本用屬性標註
        Header("示範三：Class 來源（AgentClassSkill ＋ [AgentSkillResource]／[AgentSkillScript]）");
        using (var classProvider = new AgentSkillsProvider([new WeeklyReportSkill()], TrustedSkillOptions()))
        {
            stats.AddRange(await RunBothQuestionsAsync("Class", CreateAgent(chatClient, "週報助理（Class）", classProvider)));
        }

        // ── 對照組：不用技能包，把 SKILL.md、範本、用語規範「全部」塞進 instructions
        Header("對照組：不用技能包，完整技能內容全部塞進 instructions");
        var fullInstructions = string.Join(Environment.NewLine + Environment.NewLine,
            BaseInstructions,
            "以下是寫週報的完整規範，使用者要求寫週報時請遵守：",
            File.ReadAllText(Path.Combine(skillsFolder, "weekly-report", "SKILL.md")),
            File.ReadAllText(Path.Combine(skillsFolder, "weekly-report", "assets", "weekly-report-template.md")),
            File.ReadAllText(Path.Combine(skillsFolder, "weekly-report", "references", "writing-guide.md")));
        AIAgent baselineAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "週報助理（全塞 instructions）",
            ChatOptions = new ChatOptions { Instructions = fullInstructions }
        });
        stats.AddRange(await RunBothQuestionsAsync("全塞 instructions", baselineAgent));

        PrintSummary(stats);
    }

    // 本範例的技能都是自己寫的、可信任的，所以關掉三個技能工具的核准；
    // 預設三個工具都「需要核准」，若不關掉，代理會停在 ToolApprovalRequestContent 等人回覆（參考 MFA011）。
    // 另一種做法是保留核准，再用 UseToolApproval() 搭配 AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule 自動放行唯讀工具。
    private static AgentSkillsProviderOptions TrustedSkillOptions() => new()
    {
        DisableLoadSkillApproval = true,
        DisableReadSkillResourceApproval = true,
        DisableRunSkillScriptApproval = true,
    };

    // 技能包就是一個 AIContextProvider：跟 MFA013 的 CompactionProvider 一樣掛在 AIContextProviders
    private static AIAgent CreateAgent(IChatClient chatClient, string name, AgentSkillsProvider skills) =>
        new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions { Instructions = BaseInstructions },
            AIContextProviders = [skills]
        });

    private static async Task<List<QuestionStat>> RunBothQuestionsAsync(string agentLabel, AIAgent agent)
    {
        var result = new List<QuestionStat>();

        foreach (var (key, question) in new[] { ("A", QuestionA), ("B", QuestionB) })
        {
            Section($"{agentLabel}｜問題 {key}：{FirstLine(question)}");

            current = new QuestionStat(agentLabel, key);
            AgentResponse response = await agent.RunAsync(question); // 每題獨立，不共用 session，token 數字才乾淨
            result.Add(current);

            Console.WriteLine("  [代理回答]");
            Console.WriteLine(Indent(response.Text, "    "));
            Console.WriteLine($"  小計：呼叫 LLM {current.LlmCalls} 次｜技能工具：{ToolList(current)}｜" +
                              $"輸入 token {current.InputTokens}／輸出 token {current.OutputTokens}");
        }

        return result;
    }

    // ── 觀察用中介層：印出每一次往返 LLM 時「代理到底看到了什麼、要求了什麼」
    private static async Task<ChatResponse> Observe(
        IEnumerable<ChatMessage> messages, ChatOptions? options, IChatClient innerClient, CancellationToken cancellationToken)
    {
        var list = messages.ToList();
        var stat = current!;
        stat.LlmCalls++;

        var toolNames = options?.Tools?.Select(t => t.Name).ToList() ?? [];
        Console.WriteLine($"  ▶ 第 {stat.LlmCalls} 次呼叫 LLM：送出 {list.Count} 則訊息｜可用工具：" +
                          (toolNames.Count > 0 ? string.Join("、", toolNames) : "（無）"));

        // 系統提示詞 = ChatOptions.Instructions ＋ 系統訊息；AgentSkillsProvider 會在這裡加上 <available_skills> 摘要
        var systemPrompt = string.Join(Environment.NewLine,
            new[] { options?.Instructions }
                .Concat(list.Where(m => m.Role == ChatRole.System).Select(m => m.Text))
                .Where(text => !string.IsNullOrEmpty(text)));

        if (stat.LlmCalls == 1)
        {
            stat.FirstSystemPromptChars = systemPrompt.Length;
            Console.WriteLine($"    系統提示詞長度：{systemPrompt.Length} 字");

            // 第一題的第一次呼叫印出技能摘要，證明代理平時只看到 name 與 description
            var skillsBlock = Regex.Match(systemPrompt, "<available_skills>.*?</available_skills>", RegexOptions.Singleline);
            if (stat.Question == "A" && skillsBlock.Success)
            {
                Console.WriteLine("    系統提示詞裡的技能摘要（代理平時只看得到這些）：");
                Console.WriteLine(Indent(skillsBlock.Value, "      │ "));
            }
        }

        // 上一輪工具執行完的結果，會在這一次呼叫被帶回給 LLM：印出是哪個工具、多少字
        foreach (var functionResult in list.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                     .Where(r => stat.SeenResults.Add(r.CallId)))
        {
            var text = ResultText(functionResult.Result);
            stat.CallNames.TryGetValue(functionResult.CallId, out var toolName);
            Console.WriteLine($"    ◀ 帶回工具結果：{toolName ?? functionResult.CallId}（{text.Length} 字）→ {Preview(text)}");

            // load_skill 的回傳就是完整技能內容，尾端會附上 <available_resources>／<available_scripts> 清單
            if (toolName == AgentSkillsProvider.LoadSkillToolName)
            {
                foreach (var line in text.Split('\n').Select(l => l.Trim())
                             .Where(l => l.StartsWith("<available_") || l.StartsWith("<resource") || l.StartsWith("<script")))
                {
                    Console.WriteLine($"      │ {line}");
                }
            }
        }

        ChatResponse response = await innerClient.GetResponseAsync(list, options, cancellationToken);

        stat.InputTokens += response.Usage?.InputTokenCount ?? 0;
        stat.OutputTokens += response.Usage?.OutputTokenCount ?? 0;

        // LLM 這一輪要求呼叫哪些工具（load_skill／read_skill_resource／run_skill_script）
        foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
        {
            stat.CallNames[call.CallId] = call.Name;
            var arguments = call.Arguments is null ? "" : JsonSerializer.Serialize(call.Arguments, JsonOptions);
            stat.ToolCalls.Add(call.Name);
            Console.WriteLine($"    ✦ LLM 要求呼叫：{call.Name} {arguments}");
        }

        Console.WriteLine($"    token：輸入 {response.Usage?.InputTokenCount}／輸出 {response.Usage?.OutputTokenCount}");
        return response;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 中文不要被轉成 \uXXXX
    };

    private static string ResultText(object? result) => result switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        _ => result.ToString() ?? ""
    };

    private static void PrintSummary(List<QuestionStat> stats)
    {
        Header("對照小結");

        Console.WriteLine("  ┌ 問題 A（與週報無關）：技能包代理只帶著摘要，對照組則每次都帶著完整規範");
        foreach (var s in stats.Where(s => s.Question == "A")) PrintSummaryRow(s);

        Console.WriteLine("  ┌ 問題 B（寫週報）：技能包代理需要時才載入完整內容與資源");
        foreach (var s in stats.Where(s => s.Question == "B")) PrintSummaryRow(s);

        var baselineA = stats.FirstOrDefault(s => s.Agent == "全塞 instructions" && s.Question == "A");
        var skillA = stats.Where(s => s.Agent != "全塞 instructions" && s.Question == "A").ToList();
        if (baselineA is not null && skillA.Count > 0)
        {
            var averageSkillA = skillA.Average(s => s.InputTokens);
            Console.WriteLine();
            Console.WriteLine($"  問題 A：技能包代理平均輸入 {averageSkillA:0} token，對照組 {baselineA.InputTokens} token，" +
                              $"每次無關的對話省下約 {baselineA.InputTokens - averageSkillA:0} token。");
        }

        Console.WriteLine();
        Console.WriteLine("  注意：技能包代理本身有固定成本——AgentSkillsProvider 預設的技能說明提示詞，加上 load_skill 等三個工具定義。");
        Console.WriteLine("        兩邊系統提示詞的「字數」差不多，token 卻差很多：預設說明提示詞是英文（約 4 個字元 1 個 token），");
        Console.WriteLine("        而對照組多送的是中文範本與規範（約 1 個字 1 個 token），這些內容技能包代理平常完全不必送。");
        Console.WriteLine();
        Console.WriteLine("  觀察：技能包把「摘要常駐、內容按需載入」——平常每一輪只多付 name＋description 的 token；");
        Console.WriteLine("        真的要寫週報時才多幾次往返去 load_skill／read_skill_resource／run_skill_script。");
        Console.WriteLine("        技能越多、內容越長（範本、規範、參考文件），平常省下的就越多；");
        Console.WriteLine("        反過來說，若每一題都會用到同一個技能，把它直接寫進 instructions 反而比較省往返。");
    }

    private static void PrintSummaryRow(QuestionStat s) =>
        Console.WriteLine($"  │ {s.Agent,-18}｜系統提示詞 {s.FirstSystemPromptChars,5} 字｜呼叫 LLM {s.LlmCalls} 次｜" +
                          $"輸入 token {s.InputTokens,6}｜技能工具：{ToolList(s)}");

    private static string ToolList(QuestionStat stat) =>
        stat.ToolCalls.Count == 0 ? "（無）" : string.Join(" → ", stat.ToolCalls);

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    private static string Preview(string text)
    {
        var oneLine = Regex.Replace(text, @"\s+", " ").Trim();
        return oneLine.Length <= 60 ? oneLine : oneLine[..60] + "…";
    }

    private static string Indent(string text, string prefix) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(line => prefix + line.TrimEnd('\r')));

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
}

/// <summary>一個代理回答一題的統計（由觀察用中介層累加）。</summary>
internal sealed class QuestionStat(string agent, string question)
{
    public string Agent { get; } = agent;
    public string Question { get; } = question;
    public int LlmCalls { get; set; }
    public int FirstSystemPromptChars { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public List<string> ToolCalls { get; } = [];
    public Dictionary<string, string> CallNames { get; } = [];
    public HashSet<string> SeenResults { get; } = [];
}

/// <summary>
/// Class 來源的「寫週報」技能：繼承 AgentClassSkill&lt;T&gt;（T 填自己，CRTP 寫法），
/// 用屬性標註資源與腳本，框架會用反射自動找出來，列進 load_skill 回傳內容的
/// &lt;available_resources&gt;／&lt;available_scripts&gt; 區塊。
/// </summary>
internal sealed class WeeklyReportSkill : AgentClassSkill<WeeklyReportSkill>
{
    // 摘要（name + description）：這是代理「平時」唯一看得到的部分，會被放進系統提示詞的 <available_skills>
    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        WeeklyReportContent.SkillName,
        WeeklyReportContent.Description);

    // 完整指示：只有模型呼叫 load_skill("weekly-report") 時才會送進上下文
    protected override string Instructions => WeeklyReportContent.CodeInstructions("Class");

    // 靜態資源：用屬性提供
    [AgentSkillResource("template")]
    [Description("週報範本（Markdown），必須照這份範本的標題與順序輸出")]
    public string Template => WeeklyReportContent.Template;

    [AgentSkillResource("writing-guide")]
    [Description("週報用語規範")]
    public string WritingGuide => WeeklyReportContent.WritingGuide;

    // 動態資源：用方法提供，每次 read_skill_resource 都會重新執行，所以日期永遠是「今天所在的這一週」
    [AgentSkillResource("this-week")]
    [Description("本週一到週五的日期範圍")]
    public string ThisWeek() => WeeklyReportContent.ThisWeek();

    // 腳本：模型透過 run_skill_script 呼叫，參數由 AIFunctionFactory 依方法簽章自動產生 JSON schema
    [AgentSkillScript("completion-rate")]
    [Description("依完成項數與總項數計算本週完成率，並回傳燈號")]
    private static string CompletionRate(
        [Description("本週已完成的項數")] int done,
        [Description("本週排定的總項數")] int total) =>
        WeeklyReportContent.CompletionRate(done, total);
}

/// <summary>
/// Inline 與 Class 兩種來源共用的技能內容。內容刻意與 skills/weekly-report 資料夾的 File 版本一致，
/// 三種來源的差別只在「技能是怎麼定義、怎麼交給 AgentSkillsProvider 的」。
/// </summary>
internal static class WeeklyReportContent
{
    public const string SkillName = "weekly-report";

    public const string Description =
        "把零散的本週工作筆記整理成格式一致的工程師週報（本週完成、進行中、風險與需求、下週計畫）。使用者要求寫週報、整理本週進度時使用。";

    public static string CodeInstructions(string sourceKind) => $"""
        # 寫週報（{sourceKind} 來源）

        你現在載入的是「寫週報」技能的完整內容。請依照下列步驟產出週報：

        1. 先用 `read_skill_resource` 讀取 `template`，週報必須完全照這份範本的標題與順序輸出。
        2. 再用 `read_skill_resource` 讀取 `writing-guide`，遵守裡面的用語規範。
        3. 用 `read_skill_resource` 讀取 `this-week` 取得本週日期範圍，填進標題。
        4. 筆記裡有「完成 X 項、共 Y 項」之類的數字時，務必用 `run_skill_script` 呼叫 `completion-rate` 計算完成率與燈號，不要自己心算。
        5. 把使用者的工作筆記逐條歸類到「本週完成／進行中／風險與需求／下週計畫」四個區塊。
        6. 筆記沒提到的內容不要自己編；區塊沒有內容時寫「無」。
        7. 只輸出週報本身，不要加開場白或結語。
        """;

    // 與 skills/weekly-report/assets/weekly-report-template.md 相同（Inline／Class 版多了燈號欄位）
    public const string Template = """
        # 週報｜{姓名}｜{本週日期範圍}

        **本週完成率：** {完成數}/{總數}（{百分比}%）{燈號}

        ## 一、本週完成
        - {項目}：{成果，盡量量化}

        ## 二、進行中
        - {項目}：{目前進度}，預計 {完成時間}

        ## 三、風險與需求
        - {風險或需要的協助}（需要誰：{對象}）

        ## 四、下週計畫
        - {項目}
        """;

    // 與 skills/weekly-report/references/writing-guide.md 相同
    public const string WritingGuide = """
        # 週報用語規範

        - 每一條以動詞開頭，例如「完成」「修正」「導入」「評估」，不要寫成流水帳。
        - 能量化就量化：寫「API 平均回應時間從 820ms 降到 310ms」，不要只寫「效能變好」。
        - 一條只講一件事，每條不超過 40 個字。
        - 進行中的項目一定要附「預計完成時間」。
        - 風險要寫出「影響」與「需要誰協助」，不要只寫「有點卡」。
        - 使用繁體中文與台灣慣用的技術用語（例如「資料庫」「程式碼」「部署」）。
        """;

    /// <summary>動態資源：以今天為準，算出本週一到週五（例如 2026/11/02（一）～2026/11/06（五））。</summary>
    public static string ThisWeek()
    {
        var today = DateTime.Today;
        int offset = ((int)today.DayOfWeek + 6) % 7; // 週一 = 0 … 週日 = 6
        var monday = today.AddDays(-offset);
        var friday = monday.AddDays(4);
        return $"{monday:yyyy/MM/dd}（一）～{friday:yyyy/MM/dd}（五）";
    }

    /// <summary>腳本：計算完成率並給燈號（≥ 80% 綠燈、≥ 50% 黃燈、其餘紅燈）。</summary>
    public static string CompletionRate(int done, int total)
    {
        if (total <= 0) return "總項數必須大於 0";
        if (done < 0 || done > total) return "完成項數必須介於 0 到總項數之間";

        double rate = Math.Round(done * 100.0 / total, MidpointRounding.AwayFromZero);
        string light = rate >= 80 ? "🟢 綠燈" : rate >= 50 ? "🟡 黃燈" : "🔴 紅燈";
        return string.Create(CultureInfo.InvariantCulture, $"完成率 {done}/{total}（{rate:0}%），燈號：{light}");
    }
}
