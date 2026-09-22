using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace csWorkflowAgent;

internal class Program
{
    internal const string Topic = "天空為什麼是藍色的";

    internal const int SectionCount = 3;

    // 審稿回圈的上限。達到上限就強制發布 —— 沒有這條線，回圈可能永遠跑不完。
    internal const int MaxRounds = 2;

    private const int PromptPreviewChars = 400;

    private const int FeedbackPreviewChars = 60;

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

        // ────────────────────────────────────────────────────────────────
        // 一、三個代理：各司其職，共用同一個聊天用戶端
        // ────────────────────────────────────────────────────────────────
        AIAgent outlineAgent = CreateJsonAgent(
            chatClient,
            "大綱規劃師",
            $"你是兒童科普文章的大綱規劃師。針對使用者給的主題，規劃剛好 {SectionCount} 個段落小標，" +
            "每段各談一個面向、彼此不可重疊，並用小學中高年級學生看得懂的說法。只輸出 JSON。",
            typeof(OutlineReply));

        AIAgent writerAgent = new ChatClientAgent(
            chatClient,                                      // 聊天用戶端
            "你是兒童科普作家，讀者是小學中高年級學生。" +
            "用生活中看得到的例子解釋，句子短、不用專業術語，一律使用繁體中文。",  // 系統提示詞 instructions
            "撰稿員",                                        // 代理名稱   name
            null);                                           // 代理描述   description

        AIAgent reviewAgent = CreateJsonAgent(
            chatClient,
            "審稿員",
            // ⚠️「嚴格」與 85 分門檻是刻意的：審稿夠嚴，首輪才會退稿，
            //    範例才示範得出「審稿 → 改稿 → 審稿」那個回圈。放寬門檻等於讓示範消失。
            "你是**嚴格**的兒童科普編輯，讀者是小學中高年級學生。逐項檢查：有沒有超出年齡的詞彙、" +
            "比喻是否精確（不能為了好懂就講錯）、段落之間是否連貫。" +
            "評分 0~100，**85 分以上才算通過**；只要有一項不到位就給 passed=false，" +
            "並在 feedback 寫出具體可執行的修改指示。只輸出 JSON。",
            typeof(ReviewReply));

        // ────────────────────────────────────────────────────────────────
        // 二、六個執行器：工作流圖上的節點
        // ────────────────────────────────────────────────────────────────
        var outline = new OutlineExecutor(outlineAgent);

        ExecutorBinding[] writers =
            [.. Enumerable.Range(0, SectionCount).Select(i => new SectionWriterExecutor(i, writerAgent))];

        var assemble = new AssembleExecutor(SectionCount);
        var review = new ReviewExecutor(reviewAgent);
        var revise = new ReviseExecutor(writerAgent);
        var publish = new PublishExecutor();

        // ────────────────────────────────────────────────────────────────
        // 三、組圖 —— 這段就是整個範例的重點
        //
        //   主題 ─► 大綱 ─┬─► 寫手1 ─┐
        //                 ├─► 寫手2 ─┼─► 組稿 ─► 審稿 ─┬─(過關)─► 發布
        //                 └─► 寫手3 ─┘                 └─(退稿)─► 改稿 ─┐
        //                                                   ▲           │
        //                                                   └───────────┘
        //
        // 編排長什麼樣子，在這裡是「宣告」出來的資料，不是散落在 if/else 裡的控制流程。
        // ────────────────────────────────────────────────────────────────
        Workflow workflow = new WorkflowBuilder(outline)
            .AddFanOutEdge(outline, writers)
            .AddFanInBarrierEdge(writers, assemble, "三段到齊才放行")
            .AddEdge(assemble, review)
            .AddEdge<ReviewResult>(review, publish,
                r => r is not null && (r.Passed || r.Draft.Round >= MaxRounds))
            .AddEdge<ReviewResult>(review, revise,
                r => r is not null && !r.Passed && r.Draft.Round < MaxRounds)
            .AddEdge(revise, review)
            .WithOutputFrom(publish)
            .Build();

        Header("工作流拓撲圖（Mermaid）");
        Console.WriteLine(WorkflowVisualizer.ToMermaidString(workflow));

        // ────────────────────────────────────────────────────────────────
        // 四、執行並觀察事件
        //
        // 每一站都用三段式講清楚它做了什麼：收到什麼 → 問了 LLM 什麼 → 產出什麼。
        // 前後兩段是內建事件本來就帶著的資料（Data），中間那段得靠執行器自己回報。
        //
        // 注意三個寫手的事件會交錯出現 —— 那不是壞掉，正是扇出真的平行的證據，
        // 所以每一行都把執行器名稱標出來。
        // ────────────────────────────────────────────────────────────────
        Header($"開始執行工作流 / 使用的模型: {model}");
        Say("主題", Topic);
        Console.WriteLine();

        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, Topic);

        await foreach (WorkflowEvent evt in run.WatchStreamAsync())
        {
            switch (evt)
            {
                // 收到什麼：ExecutorInvokedEvent.Data 就是流進這一站的訊息
                case ExecutorInvokedEvent invoked:
                    Stage("▶", invoked.ExecutorId, "收到", Describe(invoked.Data));
                    break;

                // 問了 LLM 什麼、或組稿收件到第幾篇：執行器自己回報的
                case StageNoteEvent note:
                    Note(note.Title, note.Body);
                    break;

                // 產出什麼：ExecutorCompletedEvent.Data 就是這一站的回傳值
                case ExecutorCompletedEvent completed:
                    Stage("■", completed.ExecutorId, "產出", Describe(completed.Data));
                    break;

                case WorkflowOutputEvent output:
                    Header("最終成品");
                    Console.WriteLine(output.Data);
                    return;

                case WorkflowErrorEvent error:
                    Header("工作流發生錯誤");
                    Console.WriteLine(error.Data);
                    return;
            }
        }
    }

    private static AIAgent CreateJsonAgent(
        IChatClient chatClient, string name, string instructions, Type replyType) =>
        new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(
                    AIJsonUtilities.CreateJsonSchema(replyType)),
            },
        });

    private static string Describe(object? data) => data switch
    {
        null => "（這一步沒有往下送訊息）",
        string s => $"主題「{s}」",
        Outline o => $"{o.Sections.Length} 個小標：{string.Join(" / ", o.Sections)}",
        SectionDraft d => $"第 {d.Index + 1} 段「{d.Title}」，{d.Content.Length} 字",
        Draft d => $"第 {d.Round} 版全文，共 {d.Body.Length} 字",
        ReviewResult r =>
            $"{r.Score} 分・{(r.Passed ? "通過" : "退稿")}：{Ellipsis(r.Feedback, FeedbackPreviewChars)}",
        _ => data.ToString() ?? string.Empty,
    };

    private static string Ellipsis(string text, int maxChars)
    {
        var flat = text.ReplaceLineEndings(" ");
        return flat.Length > maxChars
            ? flat[..maxChars] + $"…（共 {flat.Length} 字）"
            : flat;
    }

    private static void Stage(string mark, string executorId, string verb, string detail)
        => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] {mark} {executorId,-4} {verb} {detail}");

    private static void Note(string title, string body)
    {
        var text = body.Length > PromptPreviewChars
            ? body[..PromptPreviewChars] + $"…（共 {body.Length} 字，此處截斷）"
            : body;

        Console.WriteLine();
        Console.WriteLine($"                   │ {title}");
        foreach (var line in text.Split('\n'))
        {
            Console.WriteLine($"                   │   {line.TrimEnd()}");
        }
    }

    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 74));
    }

    private static void Say(string who, string? text)
        => Console.WriteLine($"  {who} ▸ {text}");
}

/// <summary>大綱：主題 + 各段小標。</summary>
internal sealed record Outline(string Topic, string[] Sections);

internal sealed record SectionDraft(int Index, string Topic, string Title, string Content);

internal sealed record Draft(string Topic, string Body, int Round);

internal sealed record ReviewResult(Draft Draft, bool Passed, int Score, string Feedback);

internal sealed class StageNoteEvent(string executorId, string title, string body)
    : WorkflowEvent(body)
{
    public string ExecutorId { get; } = executorId;

    public string Title { get; } = title;

    public string Body { get; } = body;
}

internal sealed class OutlineReply
{
    [JsonPropertyName("sections")]
    public string[] Sections { get; set; } = [];
}

internal sealed class ReviewReply
{
    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("feedback")]
    public string Feedback { get; set; } = string.Empty;
}

internal sealed class OutlineExecutor(AIAgent agent) : Executor<string, Outline>("大綱")
{
    public override async ValueTask<Outline> HandleAsync(
        string topic, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var prompt = $"文章主題：{topic}";

        // 提示詞只有執行器自己知道，內建事件帶不到，所以自己回報一則
        await context.AddEventAsync(new StageNoteEvent(Id, "提示詞", prompt), cancellationToken);

        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);

        var reply = JsonSerializer.Deserialize<OutlineReply>(response.Text) ?? new OutlineReply();
        return new Outline(topic, reply.Sections);
    }
}

internal sealed class SectionWriterExecutor(int index, AIAgent agent)
    : Executor<Outline, SectionDraft>($"寫手{index + 1}")
{
    public override async ValueTask<SectionDraft> HandleAsync(
        Outline outline, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        // 大綱段數萬一和寫手數量對不上，就退回一個預設小標，不要讓整張圖炸掉
        var title = outline.Sections.ElementAtOrDefault(index) ?? $"第 {index + 1} 段";

        var prompt =
            $"文章主題：{outline.Topic}{Environment.NewLine}" +
            $"這一段的小標：{title}{Environment.NewLine}{Environment.NewLine}" +
            "請只寫這一段的內文，約 150 字。不要重述主題、不要寫結論、不要加小標。";

        await context.AddEventAsync(new StageNoteEvent(Id, "提示詞", prompt), cancellationToken);

        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);

        return new SectionDraft(index, outline.Topic, title, response.Text.Trim());
    }
}

[SendsMessage(typeof(Draft))]
internal sealed class AssembleExecutor(int expected) : Executor<SectionDraft>("組稿")
{
    private readonly List<SectionDraft> _parts = [];

    public override async ValueTask HandleAsync(
        SectionDraft part, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _parts.Add(part);

        // ⚠️ 這一行是給讀者看的關鍵：扇入節點會被叫用三次（每篇一次），
        //    沒有這則進度事件，畫面上「組稿開始→完成」連跑三次會像是程式壞了。
        await context.AddEventAsync(
            new StageNoteEvent(Id, "進度", $"收到 {_parts.Count}/{expected} 篇「{part.Title}」"),
            cancellationToken);

        if (_parts.Count < expected)
        {
            return;   // 還沒收齊，這一則就先收著，什麼都不送
        }

        var body = string.Join(
            Environment.NewLine + Environment.NewLine,
            _parts.OrderBy(p => p.Index)
                  .Select(p => $"## {p.Title}{Environment.NewLine}{p.Content}"));

        await context.SendMessageAsync(
            new Draft(part.Topic, body, Round: 1), cancellationToken: cancellationToken);
    }
}

internal sealed class ReviewExecutor(AIAgent agent) : Executor<Draft, ReviewResult>("審稿")
{
    public override async ValueTask<ReviewResult> HandleAsync(
        Draft draft, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var prompt =
            $"請審查以下文章（第 {draft.Round} 版）：{Environment.NewLine}{Environment.NewLine}" +
            draft.Body;

        await context.AddEventAsync(new StageNoteEvent(Id, "提示詞", prompt), cancellationToken);

        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);

        var reply = JsonSerializer.Deserialize<ReviewReply>(response.Text) ?? new ReviewReply();
        return new ReviewResult(draft, reply.Passed, reply.Score, reply.Feedback);
    }
}

internal sealed class ReviseExecutor(AIAgent agent) : Executor<ReviewResult, Draft>("改稿")
{
    public override async ValueTask<Draft> HandleAsync(
        ReviewResult result, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var nl = Environment.NewLine;
        var prompt =
            $"以下是文章原稿：{nl}{nl}{result.Draft.Body}{nl}{nl}" +
            $"審稿意見（{result.Score} 分）：{nl}{result.Feedback}{nl}{nl}" +
            "請依照審稿意見改寫全文，保留原有的段落結構與 ## 小標，只輸出改寫後的全文。";

        await context.AddEventAsync(new StageNoteEvent(Id, "提示詞", prompt), cancellationToken);

        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);

        // Round + 1 —— 這就是回圈的煞車，條件邊會用它判斷還能不能再退一次
        return result.Draft with { Body = response.Text.Trim(), Round = result.Draft.Round + 1 };
    }
}

[YieldsOutput(typeof(string))]
internal sealed class PublishExecutor() : Executor<ReviewResult>("發布")
{
    public override async ValueTask HandleAsync(
        ReviewResult result, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var nl = Environment.NewLine;

        var verdict = result.Passed
            ? $"審稿通過（{result.Score} 分）"
            : $"已達最大審稿輪數 {Program.MaxRounds}，以第 {result.Draft.Round} 版定稿（{result.Score} 分）";

        await context.YieldOutputAsync(
            $"【{verdict}】共 {result.Draft.Round} 版{nl}{nl}" +
            $"# {result.Draft.Topic}{nl}{nl}" +
            result.Draft.Body,
            cancellationToken);
    }
}
