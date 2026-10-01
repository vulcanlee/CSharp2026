#pragma warning disable MAAI001 // Compaction 目前仍是實驗性 API

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage; // OpenAI.Chat 也有 ChatMessage，這裡指定用 MEAI 的

namespace csCompactionAgent;

internal class Program
{
    private const string Instructions =
        "你是一位資深記者，正在逐段研讀一篇訪談稿並回答追問。請用繁體中文在 120 字內回答。" +
        "只能根據對話中出現過的內容回答；對話中找不到的資訊請直說「不記得」，不要猜。";

    // 虛構的長篇訪談稿：鵝農林阿伯的三十多年（第一段埋了最後要考的細節：12 隻、阿肥）
    private static readonly string[] Segments =
    [
        """
        記者：林阿伯，您是怎麼開始養鵝的？
        林阿伯：1987 年我退伍回到濁水溪邊的老家，田裡種稻賺不了錢。我跟舅舅借了三萬塊，
        買了 12 隻白羅曼鵝的雛鵝，就養在豬舍旁邊的空地。其中有一隻特別貪吃、走路一搖一擺，
        每天跟在我後面討飯吃，我就叫牠「阿肥」。那時候什麼都不懂，晚上還抱著手電筒去看牠們有沒有著涼，
        我媽都笑我說養鵝比養小孩還認真。
        """,
        """
        記者：後來規模是怎麼擴大的？
        林阿伯：前十年都是一點一點加，最多養到一千多隻。可是 1998 年那個颱風，溪水一夜之間漫進來，
        鵝舍整個泡在水裡，我划著塑膠桶去救，最後還是死了八百多隻。那陣子我真的想放棄，
        欠了農會一屁股債，每天凌晨三點就醒來，看著空空的鵝舍發呆。是隔壁的陳伯拉我去喝酒，
        跟我說「鵝會再生，人不能倒」，我才咬牙重新來過。
        """,
        """
        記者：重建之後，經營方式有什麼改變？
        林阿伯：我學乖了，鵝舍全部墊高一公尺，也不再只賣活鵝給盤商，因為價格被壓得很低。
        2003 年我太太在路口擺了一個小攤子賣鵝肉飯和煙燻鵝，自己養、自己殺、自己賣，
        一隻鵝的利潤多了快三倍。後來連台中的客人都專程開車來買，攤子變成店面，
        店名就叫「溪邊鵝」，到現在還是假日要排隊的店。
        """,
        """
        記者：這中間有遇過比颱風更難的關卡嗎？
        林阿伯：2015 年的禽流感。附近鵝場驗出病毒，防疫人員來了，整個場區三公里內全部撲殺，
        我們家兩千多隻鵝一天之內就沒了。那種感覺跟颱風不一樣，颱風是天災，這次是眼睜睜看著。
        政府有補償，但錢補不回心情。之後我把場區改成全密閉的負壓鵝舍，進出都要消毒換鞋，
        花了將近四百萬，但從那之後再也沒出過事。
        """,
        """
        記者：聽說現在場裡主要是女兒在管？
        林阿伯：對，小惠在台北做了八年的軟體工程師，2020 年疫情那年決定回來。
        她在鵝舍裝了溫溼度感測器和攝影機，手機上就能看每一區的狀況，飼料也改成依體重自動配給，
        一年省下快兩成成本。她還開了網路商店賣真空包的煙燻鵝，現在網購已經佔營收一半。
        我一開始覺得那些東西是花拳繡腿，後來半夜不用再起來巡場，才知道年輕人是對的。
        """,
        """
        記者：最後，您對鵝場的未來有什麼想像？
        林阿伯：我今年六十多歲了，想做的事只剩一件：讓小孩子知道食物是從哪裡來的。
        我們明年要開放鵝場導覽，跟附近三間國小合作食農教育，讓學生來餵鵝、撿鵝蛋，
        再到店裡吃一碗鵝肉飯。小惠說要做成 APP 預約，我說好啦好啦，你們年輕人決定。
        只要鵝場還在，這條溪邊就會一直有鵝叫聲，這樣我就滿足了。
        """
    ];

    // 每一段配一個預先寫好的追問：三次執行的輸入完全相同，比較才公平
    private static readonly string[] FollowUps =
    [
        "從這段看來，林阿伯當初創業最大的風險是什麼？",
        "颱風之後他能重新站起來，關鍵是什麼？",
        "自產自銷為什麼能讓利潤變成將近三倍？",
        "禽流感和颱風對他的打擊，本質上有什麼不同？",
        "小惠導入的改變裡，哪一項對林阿伯的生活影響最大？",
        "林阿伯對未來的想像，和他當初創業的動機有什麼不同？"
    ];

    // 最後兩題：一題考第一段的精確細節，一題考整篇的綜合理解
    private const string DetailQuestion = "回到第一段：林阿伯一開始養了幾隻鵝？那隻愛跟在他後面的鵝叫什麼名字？";
    private const string SynthesisQuestion = "請用三點總結整篇訪談中，林阿伯人生的重大轉折與年份。";

    // 摘要提示詞：內建的預設提示詞只要求「簡短」，每次滾動摘要都會漸漸丟掉更早的細節；
    // 但只要求「全部保留」又會讓摘要越長越接近原文、省不到 token，所以兩件事都要講清楚
    private const string SummaryPrompt =
        "你是對話摘要員。請用繁體中文把以下對話濃縮成一份摘要。" +
        "如果對話中已經有先前的摘要（[Summary] 開頭），要保留它的重點，再把新內容合併進去。" +
        "每段訪談只寫一行、不超過 40 字，只留人名、數量、金額與年份等關鍵事實，不要寫分析與問答過程。";

    // 由聊天用戶端中介層記錄：這一輪模型「實際收到」幾則訊息
    private static int lastSentMessages;

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

        // 代理用的用戶端：多包一層，數一數每次真正送給模型幾則訊息
        IChatClient agentClient = chatClient.AsBuilder().Use(CountMessages, null).Build();

        // 摘要用的用戶端：多包一層，把每次產生的摘要印出來
        IChatClient summarizerClient = chatClient.AsBuilder().Use(PrintSummary, null).Build();

        // 兩種策略用同一個觸發條件：對話超過 3 個回合就壓縮
        var trigger = CompactionTriggers.TurnsExceed(3);

        Console.WriteLine($"使用的模型：{model}／訪談共 {Segments.Length} 段，加上 2 題總結追問");

        await RunInterviewAsync(agentClient, "執行 1 / 不壓縮（對照組）", null);

        await RunInterviewAsync(agentClient, "執行 2 / 滑動視窗：只留最近 2 個回合",
            new SlidingWindowCompactionStrategy(trigger, 2));

        await RunInterviewAsync(agentClient, "執行 3 / 摘要：舊回合交給 LLM 濃縮成一則摘要",
            new SummarizationCompactionStrategy(summarizerClient, trigger, 4, SummaryPrompt));
    }

    // 同一份訪談、同一組追問，只差在掛哪一種壓縮策略
    private static async Task RunInterviewAsync(IChatClient client, string title, CompactionStrategy? strategy)
    {
        Header(title);

        AIAgent agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "訪談分析師",
            ChatOptions = new ChatOptions { Instructions = Instructions },
            // 重點就這一行：把壓縮策略包成 CompactionProvider 掛到代理上
            AIContextProviders = strategy is null ? null : [new CompactionProvider(strategy)]
        });

        var session = await agent.CreateSessionAsync();
        var rows = new List<(string Turn, int Sent, long Input, long Output)>();

        async Task AskAsync(string turn, string question, string prompt)
        {
            Say($"🧑 {turn}", question);
            var response = await agent.RunAsync(prompt, session);
            Say("🤖 分析師", response.Text);
            rows.Add((turn, lastSentMessages,
                response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0));
        }

        for (int i = 0; i < Segments.Length; i++)
        {
            // 畫面上只印追問，實際送給代理的是「整段訪談 + 追問」
            await AskAsync($"第 {i + 1} 段", FollowUps[i], $"【第 {i + 1} 段】\n{Segments[i]}\n\n問題：{FollowUps[i]}");
        }
        await AskAsync("細節題", DetailQuestion, DetailQuestion);
        await AskAsync("綜合題", SynthesisQuestion, SynthesisQuestion);

        Console.WriteLine();
        Console.WriteLine($"  【{title}】每回合統計");
        Console.WriteLine("  回合      送出訊息數  輸入 tokens  輸出 tokens");
        foreach (var row in rows)
        {
            Console.WriteLine($"  {row.Turn,-6}  {row.Sent,10}  {row.Input,11}  {row.Output,11}");
        }
        Console.WriteLine($"  {"合計",-6}  {"",10}  {rows.Sum(r => r.Input),11}  {rows.Sum(r => r.Output),11}");
    }

    // 聊天用戶端中介層：記下這次送出的訊息數，再交給內層用戶端
    private static Task<ChatResponse> CountMessages(
        IEnumerable<ChatMessage> messages, ChatOptions? options, IChatClient innerClient, CancellationToken cancellationToken)
    {
        var list = messages.ToList();
        lastSentMessages = list.Count;
        return innerClient.GetResponseAsync(list, options, cancellationToken);
    }

    // 聊天用戶端中介層：摘要策略呼叫 LLM 時，把產生的摘要與花費的 token 印出來
    private static async Task<ChatResponse> PrintSummary(
        IEnumerable<ChatMessage> messages, ChatOptions? options, IChatClient innerClient, CancellationToken cancellationToken)
    {
        var response = await innerClient.GetResponseAsync(messages, options, cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"  📝 產生摘要（輸入 {response.Usage?.InputTokenCount} ／輸出 {response.Usage?.OutputTokenCount} tokens）：");
        Console.WriteLine($"     {response.Text.ReplaceLineEndings("\n     ")}");
        return response;
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
