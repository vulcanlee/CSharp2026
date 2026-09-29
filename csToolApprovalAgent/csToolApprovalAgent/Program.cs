using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ComponentModel;
using System.Text;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage; // OpenAI.Chat 也有 ChatMessage，這裡指定用 MEAI 的

namespace csToolApprovalAgent;

internal class Program
{
    // SQLite 記憶體資料庫：連線一關資料就消失，所以整個程式共用這一條連線
    private static readonly SqliteConnection Db = new("Data Source=:memory:");

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var apiKey = Environment.GetEnvironmentVariable("AzureOpenAI_Key");
        var endpoint = Environment.GetEnvironmentVariable("AzureOpenAI_Endpoint");
        var model = "gpt-5.6-luna";

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(endpoint))
        {
            Console.WriteLine("請先設定環境變數 AzureOpenAI_Key 與 AzureOpenAI_Endpoint 後再執行。");
            return;
        }

        SeedDatabase();

        IChatClient chatClient =
            new ChatClient(
                    model,
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .AsIChatClient();

        // 1. 三個工具全部包成 ApprovalRequiredAIFunction：預設每一次呼叫都要先核准
        AIAgent innerAgent = new ChatClientAgent(
            chatClient,
            """
            你是資料庫維運助理，用繁體中文精簡回答。
            資料庫是 SQLite，只有一張資料表：Orders(Id INTEGER, Customer TEXT, Amount INTEGER, Status TEXT)。
            Status 只有四種值：Pending（待付款）、Paid（已付款）、Shipped（已出貨）、Cancelled（已取消）。
            查詢用 ExecuteSelect、修改用 ExecuteUpdate、刪除用 ExecuteDelete，每次只傳一句 SQL。
            如果操作被拒絕，請把拒絕理由轉告使用者，不要改用其他方式重試。
            """, // 系統提示詞
            "資料庫維運助理", // 代理名稱
            null, // 代理描述
            [
                new ApprovalRequiredAIFunction(AIFunctionFactory.Create(ExecuteSelect)),
                new ApprovalRequiredAIFunction(AIFunctionFactory.Create(ExecuteUpdate)),
                new ApprovalRequiredAIFunction(AIFunctionFactory.Create(ExecuteDelete)),
            ]);

        // 2. 套上 ToolApprovalAgent 中介層：AutoApprovalRules 依工具名稱分流，只有 SELECT 自動放行
        AIAgent agent = innerAgent.AsBuilder()
            .UseToolApproval(new ToolApprovalAgentOptions { AutoApprovalRules = [AutoApproveSelect] })
            .Build();

        // 核准請求與答覆要在同一個 session 裡往返，所以所有任務共用一個 session
        AgentSession session = await agent.CreateSessionAsync();

        string[] tasks =
        [
            "列出所有訂單",
            "刪除所有已取消的訂單",
            "把訂單 1002 的狀態改成已出貨",
            "再列一次所有訂單，確認目前狀態",
        ];

        Console.WriteLine($"使用的模型：{model}");

        foreach (var task in tasks)
        {
            Console.WriteLine();
            Console.WriteLine($"🧑 {task}");

            var response = await agent.RunAsync(task, session);

            // 3. 人機互動迴圈：回應裡還有核准請求，就問人，再把答覆送回同一個 session
            while (GetApprovalRequests(response) is { Count: > 0 } requests)
            {
                List<AIContent> answers = [.. requests.Select(AskHuman)];
                response = await agent.RunAsync(new ChatMessage(ChatRole.User, answers), session);
            }

            Console.WriteLine($"🤖 {response.Text}");
        }
    }

    // AutoApprovalRules：只看工具名稱，ExecuteSelect 回傳 true（放行），其他回傳 false（交給人決定）
    private static ValueTask<bool> AutoApproveSelect(ToolAutoApprovalRuleContext context)
    {
        var name = context.FunctionCallContent.Name;
        var approved = name == nameof(ExecuteSelect);
        Console.WriteLine(approved ? $"⚡ {name} 自動放行" : $"✋ {name} 需要人工核准");
        return ValueTask.FromResult(approved);
    }

    private static List<ToolApprovalRequestContent> GetApprovalRequests(AgentResponse response) =>
        [.. response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>()];

    // 顯示代理想執行的 SQL，讓人按 y/n 決定；拒絕時可以輸入理由，理由會回傳給模型
    private static AIContent AskHuman(ToolApprovalRequestContent request)
    {
        var call = (FunctionCallContent)request.ToolCall;
        Console.WriteLine($"🔐 代理要求執行 {call.Name}");
        Console.WriteLine($"   SQL: {call.Arguments?["sql"]}");
        Console.Write("   是否核准？(y/n) > ");

        if (Console.ReadLine()?.Trim().ToLowerInvariant() == "y")
        {
            return request.CreateResponse(true);
        }

        Console.Write("   拒絕理由（直接 Enter 使用預設）> ");
        var reason = Console.ReadLine();
        return request.CreateResponse(false, string.IsNullOrWhiteSpace(reason) ? "DBA 拒絕執行" : reason);
    }

    // AutoApprovalRules 只比對工具名稱，擋不住「把 DELETE 塞進 ExecuteSelect」，所以工具本身也要檢查 SQL 開頭
    [Description("執行唯讀的 SELECT 查詢，回傳查詢結果")]
    private static string ExecuteSelect([Description("一句 SELECT 語法")] string sql)
    {
        Console.WriteLine($"🔧 呼叫 ExecuteSelect(sql: {sql})");
        if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return "錯誤：ExecuteSelect 只接受 SELECT 語法";
        }

        using var command = Db.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var columns = Enumerable.Range(0, reader.FieldCount);
        var result = new StringBuilder();
        result.AppendLine(string.Join(" | ", columns.Select(reader.GetName)));
        while (reader.Read())
        {
            result.AppendLine(string.Join(" | ", columns.Select(reader.GetValue)));
        }
        return result.ToString();
    }

    [Description("執行 UPDATE 修改資料（需要 DBA 核准）")]
    private static string ExecuteUpdate([Description("一句 UPDATE 語法")] string sql)
    {
        Console.WriteLine($"🔧 呼叫 ExecuteUpdate(sql: {sql})");
        return ExecuteNonQuery("UPDATE", sql);
    }

    [Description("執行 DELETE 刪除資料（需要 DBA 核准）")]
    private static string ExecuteDelete([Description("一句 DELETE 語法")] string sql)
    {
        Console.WriteLine($"🔧 呼叫 ExecuteDelete(sql: {sql})");
        return ExecuteNonQuery("DELETE", sql);
    }

    private static string ExecuteNonQuery(string verb, string sql)
    {
        if (!sql.TrimStart().StartsWith(verb, StringComparison.OrdinalIgnoreCase))
        {
            return $"錯誤：只接受 {verb} 語法";
        }

        using var command = Db.CreateCommand();
        command.CommandText = sql;
        return $"{verb} 完成，影響 {command.ExecuteNonQuery()} 筆資料";
    }

    private static void SeedDatabase()
    {
        Db.Open();
        using var command = Db.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE Orders (Id INTEGER PRIMARY KEY, Customer TEXT, Amount INTEGER, Status TEXT);
            INSERT INTO Orders VALUES
                (1001, '王小明', 1200, 'Paid'),
                (1002, '陳美麗',  850, 'Pending'),
                (1003, '林大華', 3200, 'Cancelled'),
                (1004, '張志強',  560, 'Shipped'),
                (1005, '李雅婷', 1500, 'Cancelled');
            """;
        command.ExecuteNonQuery();
    }
}
