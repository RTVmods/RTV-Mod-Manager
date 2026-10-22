// How a ManagerSession asks the user something in the middle of a flow
// (confirm a delete, pick which mods to move to a newer version, ...).
// The front end implements it with its own dialogs; tests implement it
// with canned answers. Calls are synchronous and come on the thread the
// session method was called on — the UI thread in the app — the same
// way the WinForms front end showed its modal message boxes.

namespace VostokModManager.Services;

public enum PromptKind { Info, Warning, Error, Question }

/// <summary>One row of a checklist prompt. `Value` is handed back for
/// every row the user leaves ticked.</summary>
public sealed record PickItem<T>(T Value, string Label, string Detail = "", bool Checked = true);

public interface IUserPrompt
{
    /// <summary>A message with an OK button.</summary>
    void Inform(string title, string message, PromptKind kind = PromptKind.Info);

    /// <summary>Yes / No. True for Yes.</summary>
    bool Confirm(string title, string message, PromptKind kind = PromptKind.Question);

    /// <summary>One of several labelled choices. Returns the chosen
    /// index, or -1 when the user cancels.</summary>
    int Choose(string title, string message, IReadOnlyList<string> options, PromptKind kind = PromptKind.Question);

    /// <summary>A checklist. Returns the ticked values, or null when
    /// the user cancels.</summary>
    IReadOnlyList<T>? Pick<T>(string title, string message, IReadOnlyList<PickItem<T>> items, string okLabel = "OK");

    /// <summary>A one-line (or, with `multiline`, a free-form) text
    /// answer, or null when the user cancels.</summary>
    string? AskText(string title, string prompt, string initial = "", bool multiline = false);

    /// <summary>An error that ended an action.</summary>
    void Error(string title, Exception ex) => Inform(title, ex.Message, PromptKind.Error);
}
