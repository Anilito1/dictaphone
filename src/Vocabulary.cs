namespace Dictee;

internal static class Vocabulary
{
    public static readonly string FilePath = Path.Combine(Settings.Folder, "vocab.txt");
    const int MaxPromptLength = 800;

    const string Defaults = """
        # Un terme par ligne : noms propres, jargon technique, marques...
        # Ils sont envoyés au modèle pour être reconnus et bien orthographiés.
        # Les modifications s'appliquent immédiatement, sans redémarrer.
        Claude Code
        Codex
        Anthropic
        Supabase
        Edge Function
        Next.js
        React
        TypeScript
        JavaScript
        Tailwind
        Node.js
        npm
        GitHub
        commit
        push
        pull request
        merge
        rebase
        deploy
        API
        endpoint
        webhook
        JSON
        SQL
        RLS
        migration
        frontend
        backend
        refactor
        debug
        Shopify
        Cloudflare
        Ringover
        Mailchimp
        Meta Ads
        WhatsApp
        Coiffissimo
        """;

    public static void EnsureFile()
    {
        if (File.Exists(FilePath)) return;
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(FilePath, Defaults + Environment.NewLine);
    }

    public static string BuildPrompt()
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(FilePath);
        }
        catch (IOException)
        {
            return "";
        }

        var terms = lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));
        var joined = string.Join(", ", terms);
        if (joined.Length == 0) return "";

        var prompt = $"Dictée en français, bien ponctuée. Vocabulaire : {joined}.";
        return prompt.Length > MaxPromptLength ? prompt[..MaxPromptLength] : prompt;
    }
}
