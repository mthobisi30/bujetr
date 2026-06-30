using System;
using System.Text.RegularExpressions;
using CognitiveBudget.Web.Models.Domain;

namespace CognitiveBudget.Web.Utilities;

/// <summary>
/// Presentation helpers for turning enums and domain values into
/// human-friendly text/markup for the views.
/// </summary>
public static class DisplayHelpers
{
    /// <summary>Splits PascalCase into spaced words, e.g. "SpendingThreshold" → "Spending threshold".</summary>
    public static string Humanize(this Enum value)
    {
        var name = value.ToString();
        var spaced = Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", " $1");
        return char.ToUpperInvariant(spaced[0]) + spaced.Substring(1).ToLowerInvariant();
    }

    /// <summary>A small emoji cue for a mood, used in transaction lists.</summary>
    public static string MoodEmoji(this EmotionalState? state) => state switch
    {
        EmotionalState.Happy    => "😊",
        EmotionalState.Neutral  => "😐",
        EmotionalState.Stressed => "😣",
        EmotionalState.Anxious  => "😰",
        EmotionalState.Bored    => "😑",
        EmotionalState.Sad      => "😢",
        EmotionalState.Excited  => "🤩",
        _                       => ""
    };

    /// <summary>Bootstrap contextual colour for a mood badge.</summary>
    public static string MoodCssClass(this EmotionalState? state) => state switch
    {
        EmotionalState.Happy or EmotionalState.Excited => "success",
        EmotionalState.Neutral                         => "secondary",
        EmotionalState.Bored                           => "warning",
        EmotionalState.Stressed or EmotionalState.Anxious or EmotionalState.Sad => "danger",
        _                                              => "light"
    };
}
