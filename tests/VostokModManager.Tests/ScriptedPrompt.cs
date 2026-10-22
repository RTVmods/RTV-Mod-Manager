using VostokModManager.Services;

namespace VostokModManager.Tests;

/// <summary>An IUserPrompt with canned answers that records every
/// question it was asked, so a test can check both the outcome and that
/// the user was (or wasn't) asked.</summary>
public sealed class ScriptedPrompt : IUserPrompt
{
    public bool ConfirmAnswer { get; set; } = true;
    public int ChooseAnswer { get; set; } = 0;
    public string? TextAnswer { get; set; }

    /// <summary>When set, decides what a checklist returns; by default
    /// the items' own ticks are accepted as they are.</summary>
    public Func<IReadOnlyList<object>, IReadOnlyList<object>?>? PickAnswer { get; set; }

    public List<string> Asked { get; } = new();
    public List<(string Title, string Message, PromptKind Kind)> Messages { get; } = new();

    public void Inform(string title, string message, PromptKind kind = PromptKind.Info)
    {
        Messages.Add((title, message, kind));
        Asked.Add("inform:" + title);
    }

    public bool Confirm(string title, string message, PromptKind kind = PromptKind.Question)
    {
        Messages.Add((title, message, kind));
        Asked.Add("confirm:" + title);
        return ConfirmAnswer;
    }

    public int Choose(string title, string message, IReadOnlyList<string> options, PromptKind kind = PromptKind.Question)
    {
        Messages.Add((title, message, kind));
        Asked.Add("choose:" + title);
        return ChooseAnswer;
    }

    public IReadOnlyList<T>? Pick<T>(string title, string message, IReadOnlyList<PickItem<T>> items, string okLabel = "OK")
    {
        Messages.Add((title, message, PromptKind.Question));
        Asked.Add("pick:" + title);
        if (PickAnswer == null) return items.Where(i => i.Checked).Select(i => i.Value).ToList();
        var picked = PickAnswer(items.Select(i => (object)i.Value!).ToList());
        return picked?.Cast<T>().ToList();
    }

    public string? AskText(string title, string prompt, string initial = "", bool multiline = false)
    {
        Asked.Add("text:" + title);
        return TextAnswer ?? initial;
    }

    public void Error(string title, Exception ex)
    {
        Messages.Add((title, ex.Message, PromptKind.Error));
        Asked.Add("error:" + title);
    }
}
