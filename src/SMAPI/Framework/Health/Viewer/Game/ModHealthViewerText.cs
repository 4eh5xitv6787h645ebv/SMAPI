using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StardewModdingAPI.Framework.Health.Viewer.Content;

namespace StardewModdingAPI.Framework.Health.Viewer.Game;

/// <summary>Pure width-aware text helpers for the thin game renderer.</summary>
internal static class ModHealthViewerText
{
    public const int MaximumExpandedLines = 16384;

    /// <summary>Clip text to an exact measured width, adding an ellipsis when any text is hidden.</summary>
    public static string ClipToWidth(string? text, float maximumWidth, Func<string, float> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (string.IsNullOrEmpty(text) || maximumWidth <= 0)
            return string.Empty;
        if (measure(text) <= maximumWidth)
            return text;

        const string ellipsis = "…";
        float ellipsisWidth = measure(ellipsis);
        if (ellipsisWidth > maximumWidth)
            return string.Empty;

        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int candidate = low + (high - low + 1) / 2;
            string visible = string.Concat(text.AsSpan(0, candidate), ellipsis);
            if (measure(visible) <= maximumWidth)
                low = candidate;
            else
                high = candidate - 1;
        }
        if (low > 0 && low < text.Length && char.IsHighSurrogate(text[low - 1]) && char.IsLowSurrogate(text[low]))
            low--;
        return string.Concat(text.AsSpan(0, low), ellipsis);
    }

    /// <summary>Wrap every character into measured lines without losing long unbroken values such as artifact paths.</summary>
    public static ImmutableArray<string> Wrap(string? text, float maximumWidth, Func<string, float> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (string.IsNullOrEmpty(text) || maximumWidth <= 0)
            return ImmutableArray<string>.Empty;

        List<string> lines = new(Math.Min(64, MaximumExpandedLines));
        ReadOnlySpan<char> remaining = text.AsSpan();
        while (remaining.Length > 0 && lines.Count < MaximumExpandedLines)
        {
            int newline = remaining.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> paragraph = newline >= 0 ? remaining[..newline] : remaining;
            if (paragraph.Length == 0)
                lines.Add(string.Empty);
            else
                WrapParagraph(paragraph, maximumWidth, measure, lines);

            if (newline < 0)
            {
                remaining = default;
                break;
            }
            int newlineLength = remaining[newline] == '\r' && newline + 1 < remaining.Length && remaining[newline + 1] == '\n' ? 2 : 1;
            remaining = remaining[(newline + newlineLength)..];
        }

        if (remaining.Length > 0)
            throw new InvalidOperationException($"Expanded health-report text exceeded the {MaximumExpandedLines}-line safety bound.");
        return lines.ToImmutableArray();
    }

    /// <summary>Build a stable visible cue from both semantic severity and semantic icon key.</summary>
    public static string GetRowCue(ModHealthViewerRowSeverity severity, ModHealthViewerRowIconKey icon)
    {
        string severityCue = severity switch
        {
            ModHealthViewerRowSeverity.Positive => "+",
            ModHealthViewerRowSeverity.Info => "i",
            ModHealthViewerRowSeverity.Warning => "!",
            ModHealthViewerRowSeverity.Error => "x",
            _ => "·"
        };
        string iconCue = icon switch
        {
            ModHealthViewerRowIconKey.Report => "RPT",
            ModHealthViewerRowIconKey.Privacy => "PRV",
            ModHealthViewerRowIconKey.Finding => "FND",
            ModHealthViewerRowIconKey.Capture => "CAP",
            ModHealthViewerRowIconKey.Mark => "MRK",
            ModHealthViewerRowIconKey.Mod => "MOD",
            ModHealthViewerRowIconKey.Timing => "TIM",
            ModHealthViewerRowIconKey.Callback => "CB",
            ModHealthViewerRowIconKey.Episode => "EP",
            ModHealthViewerRowIconKey.Update => "UPD",
            ModHealthViewerRowIconKey.Log => "LOG",
            ModHealthViewerRowIconKey.Failure => "ERR",
            ModHealthViewerRowIconKey.Inventory => "INV",
            ModHealthViewerRowIconKey.Environment => "ENV",
            ModHealthViewerRowIconKey.Capacity => "SIZE",
            ModHealthViewerRowIconKey.Omission => "OMIT",
            ModHealthViewerRowIconKey.Limitation => "LIM",
            _ => "ROW"
        };
        return $"[{severityCue}][{iconCue}]";
    }

    private static void WrapParagraph(ReadOnlySpan<char> paragraph, float maximumWidth, Func<string, float> measure, List<string> lines)
    {
        while (paragraph.Length > 0)
        {
            if (lines.Count >= MaximumExpandedLines)
                return;

            int low = 1;
            int high = paragraph.Length;
            while (low < high)
            {
                int candidate = low + (high - low + 1) / 2;
                if (measure(paragraph[..candidate].ToString()) <= maximumWidth)
                    low = candidate;
                else
                    high = candidate - 1;
            }

            int take = measure(paragraph[..low].ToString()) <= maximumWidth ? low : 1;
            if (take < paragraph.Length)
            {
                int whitespace = paragraph[..take].LastIndexOfAny(' ', '\t');
                if (whitespace > 0)
                    take = whitespace + 1;
            }
            if (take > 0 && take < paragraph.Length && char.IsHighSurrogate(paragraph[take - 1]) && char.IsLowSurrogate(paragraph[take]))
                take = take == 1 ? 2 : take - 1;

            lines.Add(paragraph[..take].ToString());
            paragraph = paragraph[take..];
        }
    }
}

/// <summary>A bounded page cursor over fully wrapped expanded text.</summary>
internal sealed class ModHealthViewerExpandedText
{
    private ImmutableArray<string> Lines = ImmutableArray<string>.Empty;
    private int LinesPerPage = 1;
    private float LastWidth = -1;

    public ModHealthViewerExpandedText(string title, string value)
    {
        this.Title = title ?? string.Empty;
        this.Value = value ?? string.Empty;
    }

    public string Title { get; }

    public string Value { get; }

    public int PageIndex { get; private set; }

    public int PageCount => Math.Max(1, (this.Lines.Length + this.LinesPerPage - 1) / this.LinesPerPage);

    public int CurrentLineCount => Math.Min(this.LinesPerPage, Math.Max(0, this.Lines.Length - this.PageIndex * this.LinesPerPage));

    public string GetCurrentLine(int index)
    {
        if ((uint)index >= this.CurrentLineCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return this.Lines[this.PageIndex * this.LinesPerPage + index];
    }

    public void Reflow(float width, int linesPerPage, Func<string, float> measure)
    {
        int normalizedLinesPerPage = Math.Max(1, linesPerPage);
        if (Math.Abs(width - this.LastWidth) < 0.01f && normalizedLinesPerPage == this.LinesPerPage)
            return;
        this.LastWidth = width;
        this.LinesPerPage = normalizedLinesPerPage;
        this.Lines = ModHealthViewerText.Wrap(string.IsNullOrEmpty(this.Title) ? this.Value : $"{this.Title}\n{this.Value}", width, measure);
        this.PageIndex = Math.Clamp(this.PageIndex, 0, this.PageCount - 1);
    }

    public void Apply(ModHealthViewerNavigationCommand command)
    {
        this.PageIndex = command switch
        {
            ModHealthViewerNavigationCommand.FirstRow => 0,
            ModHealthViewerNavigationCommand.LastRow => this.PageCount - 1,
            ModHealthViewerNavigationCommand.PreviousRow or ModHealthViewerNavigationCommand.PageUp or ModHealthViewerNavigationCommand.PreviousSection => Math.Max(0, this.PageIndex - 1),
            ModHealthViewerNavigationCommand.NextRow or ModHealthViewerNavigationCommand.PageDown or ModHealthViewerNavigationCommand.NextSection => Math.Min(this.PageCount - 1, this.PageIndex + 1),
            _ => this.PageIndex
        };
    }
}
