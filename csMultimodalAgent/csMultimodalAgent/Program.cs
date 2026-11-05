using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;
using System.Text.Json.Serialization;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage; // OpenAI.Chat 也有 ChatMessage，這裡指定用 MEAI 的

namespace csMultimodalAgent;

internal class Program
{
    // 照片來源：美國農業部 USDA（Wikimedia Commons，公有領域 Public domain）
    // 本機照片 fridge.jpg（1280×915）、fridge-small.jpg（500×357）：
    //   https://commons.wikimedia.org/wiki/File:Food_Safety_-_Cut_Waste_in_Refrigerators_(20200605-FSIS-LSC-0059).jpg
    // 網路照片 fridge-online.jpg（同一台冰箱、另一個角度，放在 GitHub 上用 raw 網址讀取）：
    //   https://commons.wikimedia.org/wiki/File:Food_Safety_-_Cut_Waste_in_Refrigerators_(20200605-FSIS-LSC-0017).jpg
    // 為什麼不直接用 Wikimedia 網址？Wikimedia 會擋掉模型服務端的下載請求，會得到 HTTP 400「Failed to download image」
    // 照片設定成「複製到輸出目錄」，以執行檔所在資料夾為基準讀取
    private static readonly string LocalImagePath = Path.Combine(AppContext.BaseDirectory, "fridge.jpg");
    private static readonly string SmallImagePath = Path.Combine(AppContext.BaseDirectory, "fridge-small.jpg");
    private const string FridgeImageUrl =
        "https://raw.githubusercontent.com/vulcanlee/CSharp2026/main/csMultimodalAgent/csMultimodalAgent/fridge-online.jpg";

    private const string Question = "這是我家冰箱的照片，請列出看得到的食材，並建議今天可以煮的菜。";

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

        if (!File.Exists(LocalImagePath) || !File.Exists(SmallImagePath))
        {
            Console.WriteLine("找不到 fridge.jpg 或 fridge-small.jpg，請確認照片已放在專案資料夾並複製到輸出目錄。");
            return;
        }

        IChatClient chatClient =
            new ChatClient(
                    model,
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .AsIChatClient();

        AIAgent agent = new ChatClientAgent(
            chatClient,
            "你是冰箱食材助理，請使用繁體中文。" +
            "只列出照片中看得到的食材，份量用「約 3 顆」「半盒」這類估計文字；" +
            "包裝看不出內容物時，名稱寫你的最佳判斷，並在備註寫「不確定」，不要硬猜品牌或口味。" +
            "建議 3 道家常菜，盡量只用照片中的食材；每道菜都要列出用到的現有食材與還需要另外準備的食材。",
            "冰箱食材助理",
            null);

        Console.WriteLine($"使用的模型：{model}");
        Console.WriteLine();

        // ── 示範一：DataContent ── 把本機照片的位元組直接放進訊息（送出時會轉成 Base64）
        byte[] imageBytes = File.ReadAllBytes(LocalImagePath);

        ChatMessage localMessage = new(ChatRole.User,
        [
            new TextContent(Question),
            new DataContent(imageBytes, "image/jpeg")
        ]);

        Console.WriteLine($"===== 示範一：DataContent（本機照片 fridge.jpg，{imageBytes.Length / 1024} KB）=====");
        AgentResponse<FridgeReport> localResponse = await agent.RunAsync<FridgeReport>(localMessage);
        PrintResponse(localResponse);

        // ── 示範二：UriContent ── 訊息裡只放網址，由模型服務端自己去下載圖片
        // 注意：網址必須是公開、可直接下載的圖片；內網或需要登入的網址，模型服務端是抓不到的
        ChatMessage urlMessage = new(ChatRole.User,
        [
            new TextContent(Question),
            new UriContent(new Uri(FridgeImageUrl), "image/jpeg")
        ]);

        Console.WriteLine("===== 示範二：UriContent（網路照片網址）=====");
        Console.WriteLine(FridgeImageUrl);
        Console.WriteLine();
        try
        {
            AgentResponse<FridgeReport> urlResponse = await agent.RunAsync<FridgeReport>(urlMessage);
            PrintResponse(urlResponse);
        }
        catch (ClientResultException ex)
        {
            // 圖片是模型服務端去抓的，抓不到時錯誤會從 API 回來（HTTP 400），而不是在本機發生
            Console.WriteLine($"模型服務端下載圖片失敗：{ex.Message.Trim()}");
            Console.WriteLine();
        }

        // ── 示範三：圖片大小、detail 與 token 用量 ──
        // 同一句提問，只改變附加的圖片，比較「輸入 token」；扣掉純文字的基準值，就是圖片吃掉的 token
        // detail 透過 AdditionalProperties["detail"] 傳給 OpenAI：low = 固定低解析、high = 依像素切塊計費
        Console.WriteLine("===== 示範三：圖片大小、detail 與輸入 token =====");
        const string shortQuestion = "請用一句話描述這張照片。";

        long baseline = await MeasureInputTokensAsync(agent, shortQuestion, image: null);
        Console.WriteLine($"純文字（不附圖）：輸入 {baseline} token（當作基準）");

        await PrintTokenRowAsync(agent, shortQuestion, baseline, "500×357 ／ detail=high", SmallImagePath, "high");
        await PrintTokenRowAsync(agent, shortQuestion, baseline, "1280×915／ detail=low ", LocalImagePath, "low");
        await PrintTokenRowAsync(agent, shortQuestion, baseline, "1280×915／ detail=high", LocalImagePath, "high");

        Console.WriteLine();
        Console.WriteLine("觀察：圖片 token 由「像素尺寸＋detail」決定，不是檔案的 KB 數；low 不論尺寸都是固定的小額。");
    }

    // 送出一次請求，只回傳這次的輸入 token 數
    private static async Task<long> MeasureInputTokensAsync(AIAgent agent, string question, DataContent? image)
    {
        List<AIContent> contents = [new TextContent(question)];
        if (image is not null)
        {
            contents.Add(image);
        }

        AgentResponse response = await agent.RunAsync(new ChatMessage(ChatRole.User, contents));
        return response.Usage?.InputTokenCount ?? 0;
    }

    private static async Task PrintTokenRowAsync(
        AIAgent agent, string question, long baseline, string label, string path, string detail)
    {
        byte[] bytes = File.ReadAllBytes(path);
        DataContent image = new(bytes, "image/jpeg")
        {
            AdditionalProperties = new() { ["detail"] = detail }
        };

        long inputTokens = await MeasureInputTokensAsync(agent, question, image);
        int base64Length = Convert.ToBase64String(bytes).Length;

        Console.WriteLine(
            $"{label}：檔案 {bytes.Length / 1024} KB、Base64 {base64Length:N0} 字元 → " +
            $"輸入 {inputTokens} token（圖片約 {inputTokens - baseline}）");
    }

    private static void PrintResponse(AgentResponse<FridgeReport> response)
    {
        Console.WriteLine("模型回傳的 JSON：");
        Console.WriteLine(response.Text);
        Console.WriteLine();

        Console.WriteLine("【現有食材】");
        foreach (var item in response.Result.Ingredients)
        {
            var note = string.IsNullOrWhiteSpace(item.Note) ? "" : $"（{item.Note}）";
            Console.WriteLine($"  - {item.Name}：{item.Quantity}{note}");
        }

        Console.WriteLine("【建議菜色】");
        foreach (var dish in response.Result.Dishes)
        {
            Console.WriteLine($"  ● {dish.Name}：{dish.Reason}");
            Console.WriteLine($"      用到：{string.Join("、", dish.UsedIngredients)}");
            Console.WriteLine($"      還缺：{(dish.MissingIngredients.Count == 0 ? "（無）" : string.Join("、", dish.MissingIngredients))}");
        }

        Console.WriteLine($"輸入 token：{response.Usage?.InputTokenCount}，輸出 token：{response.Usage?.OutputTokenCount}");
        Console.WriteLine();
    }
}

internal sealed class FridgeReport
{
    [JsonPropertyName("ingredients")]
    public List<FridgeItem> Ingredients { get; set; } = [];

    [JsonPropertyName("dishes")]
    public List<Dish> Dishes { get; set; } = [];
}

internal sealed class FridgeItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public string Quantity { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;
}

internal sealed class Dish
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("usedIngredients")]
    public List<string> UsedIngredients { get; set; } = [];

    [JsonPropertyName("missingIngredients")]
    public List<string> MissingIngredients { get; set; } = [];

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
