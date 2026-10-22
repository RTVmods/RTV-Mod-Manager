using System.Windows;
using System.Windows.Controls;
using VostokModManager.Services;
using VostokModManager.Wpf.Views;

namespace VostokModManager.Wpf.Services;

/// <summary>Answers the session's questions with the manager's own
/// modal window.</summary>
public sealed class DialogPrompt : IUserPrompt
{
    public Window? Owner { get; set; }

    public void Inform(string title, string message, PromptKind kind = PromptKind.Info)
        => MessageWindow.Create(Owner, title, message, kind, new[] { "OK" }, 0, 0).ShowDialog();

    public bool Confirm(string title, string message, PromptKind kind = PromptKind.Question)
    {
        var w = MessageWindow.Create(Owner, title, message, kind, new[] { "No", "Yes" }, primary: 1, cancel: 0);
        w.ShowDialog();
        return w.Result == 1;
    }

    public int Choose(string title, string message, IReadOnlyList<string> options, PromptKind kind = PromptKind.Question)
    {
        // Options read left to right; the first is the suggested one, so
        // it is drawn last (rightmost, white) with Cancel first.
        var buttons = new List<string> { "Cancel" };
        buttons.AddRange(options.Reverse());
        var w = MessageWindow.Create(Owner, title, message, kind, buttons, primary: buttons.Count - 1, cancel: 0);
        w.ShowDialog();
        if (w.Result <= 0) return -1;
        return buttons.Count - 1 - w.Result;
    }

    public IReadOnlyList<T>? Pick<T>(string title, string message, IReadOnlyList<PickItem<T>> items, string okLabel = "OK")
    {
        var panel = new StackPanel();
        var boxes = new List<(CheckBox Box, T Value)>();
        foreach (var item in items)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = item.Label, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(item.Detail))
                content.Children.Add(new TextBlock
                {
                    Text = item.Detail,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("B.Text3"),
                });
            var box = new CheckBox { IsChecked = item.Checked, Content = content, Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(box);
            boxes.Add((box, item.Value));
        }
        var w = MessageWindow.Create(Owner, title, message, PromptKind.Question, new[] { "Cancel", okLabel }, 1, 0, panel);
        w.ShowDialog();
        if (w.Result != 1) return null;
        return boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Value).ToList();
    }

    public string? AskText(string title, string prompt, string initial = "", bool multiline = false)
    {
        var box = new TextBox
        {
            Text = initial,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 120 : 0,
        };
        var w = MessageWindow.Create(Owner, title, prompt, PromptKind.Question, new[] { "Cancel", "OK" }, 1, 0, box);
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return w.Result == 1 ? box.Text : null;
    }

    public void Error(string title, Exception ex) => Inform(title, ex.Message, PromptKind.Error);
}
