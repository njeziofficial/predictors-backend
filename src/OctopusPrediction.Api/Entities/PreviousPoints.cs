namespace OctopusPrediction.Api.Entities;

// Points a user earned before this app existed (predicting elsewhere), carried over so the
// overall leaderboard reflects the whole season. Kept apart from Prediction so the scoring
// service never recalculates or wipes them. Label groups one import (e.g. "Before the app") —
// re-importing the same label updates a user's row instead of adding to it.
public class PreviousPoints
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public int Points { get; set; }
    // Exact scores the user called in that period. Counted with in-app correct scores to break
    // ties on the overall leaderboard.
    public int CorrectScores { get; set; }
    public string Label { get; set; } = string.Empty;
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
