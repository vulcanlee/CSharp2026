using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;

namespace csFunctionCallingAgent;

internal class Program
{
    private static readonly HttpClient Http = new();
    private static int _toolCallCount;

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
            "你是穿搭顧問，用繁體中文精簡回答。需要天氣資訊時請使用工具查詢，不要自己猜測天氣。", // 系統提示詞
            "穿搭顧問", // 代理名稱
            null, // 代理描述
            [AIFunctionFactory.Create(GetWeatherForecast)]); // 交給代理的工具，要不要呼叫由模型決定

        string[] questions =
        [
            "我明天要去台北上班，該穿什麼？",
            "白色襯衫搭什麼顏色的褲子好看？",
            "我明天早上在台北、晚上到高雄，要帶哪些衣服？",
        ];

        Console.WriteLine($"使用的模型：{model}");

        foreach (var question in questions)
        {
            _toolCallCount = 0;
            Console.WriteLine();
            Console.WriteLine($"❓ {question}");

            var response = await agent.RunAsync(question);

            Console.WriteLine(response.Text);
            Console.WriteLine($"（本題呼叫工具 {_toolCallCount} 次）");
        }
    }

    [Description("查詢指定城市在今天起第幾天的天氣預報")]
    private static async Task<string> GetWeatherForecast(
        [Description("城市的英文名稱，例如 Taipei、Kaohsiung")] string city,
        [Description("從今天起算第幾天：0=今天，1=明天，最多 6")] int daysFromToday)
    {
        _toolCallCount++;
        Console.WriteLine($"🔧 呼叫 GetWeatherForecast(city: {city}, daysFromToday: {daysFromToday})");

        daysFromToday = Math.Clamp(daysFromToday, 0, 6);

        // 1. 城市名稱 → 經緯度
        var geo = JsonNode.Parse(await Http.GetStringAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=zh"));
        var place = geo?["results"]?[0];
        if (place is null)
        {
            return $"找不到城市 {city}";
        }

        // 2. 經緯度 → 每日天氣預報
        var forecast = JsonNode.Parse(await Http.GetStringAsync(
            $"https://api.open-meteo.com/v1/forecast?latitude={place["latitude"]}&longitude={place["longitude"]}" +
            $"&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
            $"&timezone=auto&forecast_days={daysFromToday + 1}"));
        var daily = forecast!["daily"]!;
        var i = daysFromToday;

        return $"{place["name"]} {daily["time"]![i]}：{WeatherText((int)daily["weather_code"]![i]!)}，" +
               $"氣溫 {daily["temperature_2m_min"]![i]}~{daily["temperature_2m_max"]![i]}°C，" +
               $"降雨機率 {daily["precipitation_probability_max"]![i]}%";
    }

    // WMO 天氣代碼 → 中文
    private static string WeatherText(int code) => code switch
    {
        0 => "晴",
        1 or 2 => "多雲",
        3 => "陰",
        45 or 48 => "霧",
        >= 51 and <= 57 => "毛毛雨",
        >= 61 and <= 67 => "雨",
        >= 71 and <= 77 => "雪",
        >= 80 and <= 82 => "陣雨",
        85 or 86 => "陣雪",
        >= 95 and <= 99 => "雷雨",
        _ => "未知",
    };
}
