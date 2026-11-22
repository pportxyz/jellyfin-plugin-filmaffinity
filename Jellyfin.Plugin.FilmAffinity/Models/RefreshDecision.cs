namespace Jellyfin.Plugin.FilmAffinity.Models;

/// <summary>
/// Represents the result of evaluating an item against the FilmAffinity refresh policy.
/// </summary>
/// <param name="ShouldProcess"><c>true</c> if the item should be fetched in the current sync run.</param>
/// <param name="Priority">Lower values are processed first.</param>
/// <param name="Reason">Human-readable reason for diagnostics.</param>
public readonly record struct RefreshDecision(bool ShouldProcess, int Priority, string Reason)
{
    /// <summary>
    /// Creates a decision indicating the item should be processed.
    /// </summary>
    /// <param name="priority">The item's sync priority.</param>
    /// <param name="reason">The reason for processing.</param>
    /// <returns>A <see cref="RefreshDecision"/> with <see cref="ShouldProcess"/> set to <c>true</c>.</returns>
    public static RefreshDecision Process(int priority, string reason) => new(true, priority, reason);

    /// <summary>
    /// Creates a decision indicating the item should be skipped.
    /// </summary>
    /// <param name="reason">The reason for skipping.</param>
    /// <returns>A <see cref="RefreshDecision"/> with <see cref="ShouldProcess"/> set to <c>false</c>.</returns>
    public static RefreshDecision Skip(string reason) => new(false, int.MaxValue, reason);
}
