using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;
using System.Text.Json.Serialization;

namespace csStructuredOutputAgent;

internal class Program
{
    private const string Recipe = """
        奶油雞肉義大利麵（2 人份）

        主餐材料：
        義大利麵 200 公克
        雞胸肉 250 公克
        洋蔥 半顆
        蘑菇 100 公克
        蒜頭 2 瓣

        醬汁材料：
        蒜頭 1 瓣
        鮮奶油 150 毫升
        帕瑪森起司 30 公克
        橄欖油 1 大匙
        鹽 1 小匙
        黑胡椒 少許

        做法：
        煮熟義大利麵。用橄欖油炒香洋蔥、蘑菇與蒜頭，加入雞胸肉煎熟。
        倒入鮮奶油，加入帕瑪森起司、鹽與黑胡椒，最後拌入義大利麵。
        """;

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
            chatClient,
            "你是食譜採購助理。請從食譜的材料中整理購物清單，使用繁體中文。" +
            "只列出食譜需要的食材，包含鹽、油等調味料；做法中再次提到的食材不要重複計入。" +
            "同名食材只列一次；份量單位相同且能直接相加時合計，否則保留原份量文字，不要猜測換算。" +
            "每項食材都要有名稱和份量文字；未標示份量時填入「未註明」。" +
            "固定使用蔬果、肉類與海鮮、乳製品與蛋、主食與乾貨、調味料與其他五類；沒有食材的類別用空陣列。",
            "食譜採購助理",
            null);

        Console.WriteLine($"使用的模型：{model}");
        Console.WriteLine($"食譜：{Environment.NewLine}{Recipe}");
        Console.WriteLine($"");

        AgentResponse<ShoppingList> response = await agent.RunAsync<ShoppingList>(Recipe);

        Console.WriteLine("模型回傳的 JSON：");
        Console.WriteLine(response.Text);
        Console.WriteLine($"");

        Console.WriteLine();
        Console.WriteLine("分類購物清單：");
        Console.WriteLine($"");

        PrintCategory("蔬果", response.Result.Produce);
        PrintCategory("肉類與海鮮", response.Result.MeatAndSeafood);
        PrintCategory("乳製品與蛋", response.Result.DairyAndEggs);
        PrintCategory("主食與乾貨", response.Result.StaplesAndDryGoods);
        PrintCategory("調味料與其他", response.Result.SeasoningsAndOther);
    }

    private static void PrintCategory(string title, List<Ingredient> items)
    {
        Console.WriteLine($"【{title}】");

        if (items.Count == 0)
        {
            Console.WriteLine("  （無）");
            return;
        }

        foreach (var item in items)
        {
            Console.WriteLine($"  - {item.Name}：{item.Quantity}");
        }
    }
}

internal sealed class ShoppingList
{
    [JsonPropertyName("produce")]
    public List<Ingredient> Produce { get; set; } = [];

    [JsonPropertyName("meatAndSeafood")]
    public List<Ingredient> MeatAndSeafood { get; set; } = [];

    [JsonPropertyName("dairyAndEggs")]
    public List<Ingredient> DairyAndEggs { get; set; } = [];

    [JsonPropertyName("staplesAndDryGoods")]
    public List<Ingredient> StaplesAndDryGoods { get; set; } = [];

    [JsonPropertyName("seasoningsAndOther")]
    public List<Ingredient> SeasoningsAndOther { get; set; } = [];
}

internal sealed class Ingredient
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public string Quantity { get; set; } = string.Empty;
}
