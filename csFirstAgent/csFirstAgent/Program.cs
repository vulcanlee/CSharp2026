using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace csFirstAgent;

internal class Program
{
    static async Task Main(string[] args)
    {
        var endpoint = "https://models.github.ai/inference";
        var deploymentName = "microsoft/phi-4";
        var Github_Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "gpt-4o-mini";

        IChatClient chatClient =
            new ChatClient(
                    deploymentName,
                    new ApiKeyCredential(Github_Token!),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .AsIChatClient();

        AIAgent writer = new ChatClientAgent(
            chatClient,
            "詩人",
            "創作引人入勝、富有創意的詩。.",
            null);

        var response = await writer.RunAsync("寫一個關於鵝的詩。");

        Console.WriteLine(response.Text);
    }
}
