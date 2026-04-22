using LlmTornado;
using LlmTornado.Agents;
using LlmTornado.Chat;
using LlmTornado.Chat.Models;
using LlmTornado.Code;

public enum ReviewStatus
{
    Unknown,
    Ready,
    Revise
}

public class EditorialReview
{
    public ReviewStatus Status { get; set; } = ReviewStatus.Unknown;
    public string Rationale { get; set; } = "";
    public List<string> RevisionTasks { get; set; } = new();
}

class Program
{
    // CHANGE THIS to match the "Active Model" name shown at the top of your LM Studio
    const string ModelName = "google/gemma-3-4b";

    static async Task Main()
    {
        // PART 1: Setup connection to LM Studio
        // Added /v1 to the URL which is common for local servers
        TornadoApi api = new TornadoApi(new Uri("http://localhost:1234/v1"), "no-key-required", LLmProviders.OpenAi);

        TornadoAgent writer = CreateWriterAgent(api);
        TornadoAgent editor = CreateEditorAgent(api);

        // PART 4: UX - Welcome Message
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("======================================================");
        Console.WriteLine("   AI WRITING PIPELINE: WRITER & EDITOR AGENTS");
        Console.WriteLine("======================================================");
        Console.ResetColor();

        // PART 2: Continuous loop
        while (true)
        {
            // PART 4: UX - Command Menu / Writing Types
            Console.WriteLine("\n[ MAIN MENU ]");
            Console.WriteLine("1. Write a Tutorial");
            Console.WriteLine("2. Write an Announcement");
            Console.WriteLine("3. Write a Product Description");
            Console.WriteLine("Type 'exit' to quit.");
            Console.Write("\nSelection > ");

            string choice = Console.ReadLine()?.ToLower() ?? "";
            if (choice == "exit") break;

            string type = choice switch
            {
                "1" => "Tutorial",
                "2" => "Announcement",
                "3" => "Product Description",
                _ => "General Article"
            };

            Console.Write($"\nEnter the topic for your {type} (or 'back'): ");
            string? taskInput = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(taskInput) || taskInput.ToLower() == "back") continue;
            if (taskInput.ToLower() == "exit") break;

            string task = $"Type: {type}. Topic: {taskInput}";
            string currentDraft = "";
            int maxRounds = 3;

            // PART 3: Two-Agent Revision Pipeline
            for (int round = 1; round <= maxRounds; round++)
            {
                PrintHeader($"--- ROUND {round} ---", ConsoleColor.Yellow);

                // 1. WRITER STEP
                if (round == 1)
                {
                    Console.WriteLine("Writer is drafting...");
                    currentDraft = await GenerateDraftAsync(writer, task);
                }
                else
                {
                    Console.WriteLine("Writer is revising based on editor feedback...");
                    // We send the current draft back to the writer for improvement
                    currentDraft = await GenerateDraftAsync(writer, $"Please improve this draft based on the editor's goals: {currentDraft}");
                }

                if (currentDraft.StartsWith("ERROR")) break; // Stop if server is offline

                // PART 4: UX - Color Coded Headings & Word Count
                PrintHeader("DRAFT CONTENT:", ConsoleColor.Green);
                Console.WriteLine(currentDraft);
                Console.WriteLine($"\n[ Word Count: {CountWords(currentDraft)} ]");

                // 2. EDITOR STEP
                PrintHeader("EDITOR IS REVIEWING...", ConsoleColor.Magenta);
                EditorialReview review = await ReviewDraftAsync(editor, task, currentDraft);

                if (review.Status == ReviewStatus.Unknown)
                {
                    Console.WriteLine("Editor failed to provide a valid status. Proceeding to finalize.");
                    break;
                }

                Console.WriteLine($"STATUS: {review.Status}");
                Console.WriteLine($"RATIONALE: {review.Rationale}");

                // PART 3: Check if we stop or revise
                if (review.Status == ReviewStatus.Ready)
                {
                    Console.WriteLine("\nEditor Approved!");
                    break;
                }
                else
                {
                    Console.WriteLine("REVISION TASKS:");
                    foreach (string item in review.RevisionTasks)
                    {
                        Console.WriteLine($"- {item}");
                    }

                    if (round == maxRounds)
                        PrintHeader("MAX ROUNDS REACHED. FINALIZING.", ConsoleColor.Red);
                }
            }

            // PART 4: Final output and save to file
            PrintHeader("=== FINAL VERSION ===", ConsoleColor.Blue);
            Console.WriteLine(currentDraft);

            Console.Write("\nSave this result to 'output.txt'? (y/n): ");
            if (Console.ReadLine()?.ToLower() == "y")
            {
                await File.WriteAllTextAsync("output.txt", currentDraft);
                Console.WriteLine("File saved successfully.");
            }
        }
    }

    // --- AGENT DEFINITIONS ---

    static TornadoAgent CreateWriterAgent(TornadoApi api)
    {
        return new TornadoAgent(
            client: api,
            model: new ChatModel(ModelName),
            name: "Writer",
            instructions: "You are a professional writer. Create clear, simple, and helpful content for beginners. Return ONLY the text of the draft."
        );
    }

    static TornadoAgent CreateEditorAgent(TornadoApi api)
    {
        return new TornadoAgent(
            client: api,
            model: new ChatModel(ModelName),
            name: "Editor",
            instructions: """
            You are a strict editor. Review the draft and respond in this EXACT format:
            STATUS: [READY or REVISE]
            RATIONALE: [One short reason why]
            REVISION TASKS:
            - [List specific changes needed]
            """
        );
    }

    // --- API LOGIC WITH ERROR HANDLING ---

    static async Task<string> GenerateDraftAsync(TornadoAgent writer, string prompt)
    {
        try
        {
            Conversation conversation = await writer.Run(input: prompt);
            return GetLastAssistantText(conversation);
        }
        catch (Exception ex)
        {
            return $"ERROR: Could not connect to LM Studio. Make sure the server is running. ({ex.Message})";
        }
    }

    static async Task<EditorialReview> ReviewDraftAsync(TornadoAgent editor, string task, string draft)
    {
        try
        {
            Conversation conversation = await editor.Run(input: $"Task: {task}\nDraft: {draft}");
            string response = GetLastAssistantText(conversation);
            return ParseReview(response);
        }
        catch
        {
            return new EditorialReview { Status = ReviewStatus.Unknown, Rationale = "Connection error." };
        }
    }

    // --- UTILITIES ---

    static EditorialReview ParseReview(string response)
    {
        var review = new EditorialReview();
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bool parsingTasks = false;

        foreach (var line in lines)
        {
            if (line.Contains("STATUS:", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Contains("READY", StringComparison.OrdinalIgnoreCase)) review.Status = ReviewStatus.Ready;
                else if (line.Contains("REVISE", StringComparison.OrdinalIgnoreCase)) review.Status = ReviewStatus.Revise;
            }
            else if (line.Contains("RATIONALE:", StringComparison.OrdinalIgnoreCase))
            {
                review.Rationale = line.Split(':', 2).Last().Trim();
            }
            else if (line.Contains("REVISION TASKS:", StringComparison.OrdinalIgnoreCase))
            {
                parsingTasks = true;
            }
            else if (parsingTasks && line.Trim().StartsWith("-"))
            {
                review.RevisionTasks.Add(line.Trim().TrimStart('-').Trim());
            }
        }
        return review;
    }

    static string GetLastAssistantText(Conversation conversation)
    {
        return conversation.Messages.LastOrDefault()?.Content?.Trim() ?? "No response.";
    }

    static void PrintHeader(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine($"\n{text}");
        Console.ResetColor();
    }

    static int CountWords(string text) => text.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
}
