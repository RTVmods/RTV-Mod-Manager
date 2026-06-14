// Shared dialog-sizing safety net.
//
// Dialogs set generous default Width/Height for big monitors, but on a
// small / DPI-scaled display those defaults (and even MinimumSize) can
// exceed the screen — pushing buttons and content off the bottom/right
// where they can't be reached. ClampToWorkingArea shrinks a dialog to
// fit the screen it opens on, and lowers MinimumSize in lockstep so the
// form can actually adopt the smaller size (a MinimumSize larger than
// the screen would otherwise win).

namespace VostokModManager.Ui;

public static class DialogSizing
{
    /// <summary>Clamps the form's size (and MinimumSize) to the working
    /// area of the screen it's on, leaving a small margin. Call once
    /// after the form's Size/MinimumSize are set (e.g. end of the
    /// constructor or in OnLoad).</summary>
    public static void ClampToWorkingArea(Form f, int margin = 40)
    {
        var wa = Screen.FromControl(f).WorkingArea;
        int maxW = Math.Max(320, wa.Width - margin);
        int maxH = Math.Max(240, wa.Height - margin);

        // MinimumSize must come down first — Size can't go below it.
        if (f.MinimumSize.Width > maxW || f.MinimumSize.Height > maxH)
            f.MinimumSize = new Size(
                Math.Min(f.MinimumSize.Width, maxW),
                Math.Min(f.MinimumSize.Height, maxH));

        if (f.Width > maxW)  f.Width  = maxW;
        if (f.Height > maxH) f.Height = maxH;
    }
}
