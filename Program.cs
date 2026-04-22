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
    static async Task Main()
    {
        // Setup API connection to LM Studio
        TornadoApi api = new TornadoApi(
            new Uri("http://127.0.0.1:1234"),
            string.Empty,
            LLmProviders.OpenAi);

        TornadoAgent writer = CreateWriterAgent(api);
        TornadoAgent editor = CreateEditorAgent(api);

        // UX Improvement: Welcome Message
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================");
        Console.WriteLine("   WELCOME TO THE AI MULTI-AGENT NEWSROOM");
        Console.WriteLine("================================================");
        Console.ResetColor();

        while (true)
        {
            // UX Improvement: Command Menu / Writing Type Selection
            Console.WriteLine("\nSelect a writing type:");
            Console.WriteLine("1. Tutorial");
            Console.WriteLine("2. Announcement");
            Console.WriteLine("3. Product Description");
            Console.WriteLine("Type 'exit' to quit.");
            Console.Write("\nSelection > ");

            string choice = Console.ReadLine() ?? "";
            if (choice.ToLower() == "exit") break;

            string type = choice switch
            {
                "1" => "Tutorial",
                "2" => "Announcement",
                "3" => "Product Description",
                _ => "General Article"
            };

            Console.Write($"\nEnter the topic for your {type}: ");
            string? taskInput = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(taskInput)) continue;

            string task = $"Type: {type}. Topic: {taskInput}";
            string currentDraft = "";
            bool isApproved = false;
            int maxRounds = 3;

            // PART 3: Two-Agent Revision Pipeline
            for (int round = 1; round <= maxRounds; round++)
            {
                PrintHeader($"--- ROUND {round} ---", ConsoleColor.Yellow);

                // 1. Writer creates or revises the draft
                if (round == 1)
                {
                    currentDraft = await GenerateDraftAsync(writer, task);
                }
                else
                {
                    // For revisions, we pass the previous draft and tasks
                    currentDraft = await GenerateRevisionAsync(writer, task, currentDraft);
                }

                PrintHeader("WRITER'S DRAFT:", ConsoleColor.Green);
                Console.WriteLine(currentDraft);
                Console.WriteLine($"\n(Word Count: {CountWords(currentDraft)})"); // UX Improvement: Word Count

                // 2. Editor reviews the draft
                PrintHeader("EDITOR IS REVIEWING...", ConsoleColor.Magenta);
                EditorialReview review = await ReviewDraftAsync(editor, task, currentDraft);

                Console.WriteLine($"STATUS: {review.Status}");
                Console.WriteLine($"RATIONALE: {review.Rationale}");

                if (review.Status == ReviewStatus.Ready)
                {
                    isApproved = true;
                    break;
                }

                if (review.RevisionTasks.Count > 0)
                {
                    Console.WriteLine("REVISION TASKS:");
                    foreach (string item in review.RevisionTasks)
                    {
                        Console.WriteLine($"- {item}");
                    }
                }

                if (round == maxRounds)
                {
                    PrintHeader("REACHED MAXIMUM ROUNDS. FINALIZING...", ConsoleColor.Red);
                }
            }

            PrintHeader("=== FINAL OUTPUT ===", ConsoleColor.Blue);
            Console.WriteLine(currentDraft);

            // UX Improvement: Option to save to file
            Console.Write("\nSave to file? (y/n): ");
            if (Console.ReadLine()?.ToLower() == "y")
            {
                File.WriteAllText("output.txt", currentDraft);
                Console.WriteLine("Saved to output.txt");
            }
        }
    }

    // --- AGENT CREATION ---

    static TornadoAgent CreateWriterAgent(TornadoApi api)
    {
        return new TornadoAgent(
            client: api,
            model: new ChatModel("google/gemma-3-4b"),
            name: "Writer",
            instructions: """
            You are a professional Writer for beginner readers. 
            Your goal is to produce clear, engaging, and simple content.
            
            If you receive 'REVISION TASKS', you must rewrite the previous draft to address 
            every single point mentioned by the editor. 
            Return ONLY the revised text. Do not include conversational filler.
            """
        );
    }

    static TornadoAgent CreateEditorAgent(TornadoApi api)
    {
        return new TornadoAgent(
            client: api,
            model: new ChatModel("google/gemma-3-4b"),
            name: "Editor",
            instructions: """
            You are a strict Senior Editor. You evaluate drafts for beginner-friendliness and clarity.
            
            CRITERIA:
            1. Is the language simple? (No jargon)
            2. Is the tone appropriate for the task type?
            3. Are there clear headings or steps?

            You MUST respond in this exact format:
            STATUS: [READY or REVISE]
            RATIONALE: [One sentence explanation]
            REVISION TASKS:
            - [Bullet point task]
            
            If the draft is perfect, set STATUS: READY and leave REVISION TASKS empty.
            If there are ANY issues, set STATUS: REVISE.
            """
        );
    }

    // --- API LOGIC ---

    static async Task<string> GenerateDraftAsync(TornadoAgent writer, string task)
    {
        Conversation conversation = await writer.Run(input: $"Write a first draft for: {task}");
        return GetLastAssistantText(conversation);
    }

    static async Task<string> GenerateRevisionAsync(TornadoAgent writer, string task, string previousDraft)
    {
        // In a real scenario, you'd pass the editor's feedback here too.
        Conversation conversation = await writer.Run(input: $"Here is the previous draft: {previousDraft}. Please improve it based on the requirements for: {task}");
        return GetLastAssistantText(conversation);
    }

    static async Task<EditorialReview> ReviewDraftAsync(TornadoAgent editor, string task, string draft)
    {
        Conversation conversation = await editor.Run(input: $"TASK: {task}\nDRAFT: {draft}");
        string response = GetLastAssistantText(conversation);
        return ParseReview(response);
    }

    // --- UTILITIES ---

    // TODO 3: Robust Parsing Logic
    static EditorialReview ParseReview(string response)
    {
        var review = new EditorialReview();
        string[] lines = response.Split('\n');
        bool parsingTasks = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("STATUS:", StringComparison.OrdinalIgnoreCase))
            {
                string val = line.Replace("STATUS:", "").Trim();
                if (val.Contains("READY", StringComparison.OrdinalIgnoreCase)) review.Status = ReviewStatus.Ready;
                else review.Status = ReviewStatus.Revise;
            }
            else if (line.StartsWith("RATIONALE:", StringComparison.OrdinalIgnoreCase))
            {
                review.Rationale = line.Replace("RATIONALE:", "").Trim();
            }
            else if (line.StartsWith("REVISION TASKS:", StringComparison.OrdinalIgnoreCase))
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
        return conversation.Messages.LastOrDefault()?.Content ?? "No response received.";
    }

    static void PrintHeader(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine($"\n{text}");
        Console.ResetColor();
    }

    static int CountWords(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}