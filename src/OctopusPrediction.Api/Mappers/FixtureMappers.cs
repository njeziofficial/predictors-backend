using OctopusPrediction.Api.Dtos.Fixtures;
using OctopusPrediction.Api.Dtos.MatchWeeks;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Mappers;

public static class FixtureMappers
{
    public static FixtureDto ToDto(Fixture f) => new(
        f.Id, f.WeekId, f.HomeTeam, f.AwayTeam, f.Kickoff,
        MapStatus(f.Status),
        f.FinalScoreHome.HasValue && f.FinalScoreAway.HasValue
            ? new ScoreDto(f.FinalScoreHome.Value, f.FinalScoreAway.Value)
            : null,
        f.LiveScoreHome.HasValue && f.LiveScoreAway.HasValue
            ? new ScoreDto(f.LiveScoreHome.Value, f.LiveScoreAway.Value)
            : null,
        f.LiveMinute
    );

    public static MatchWeekDto ToDto(MatchWeek week) => new(
        week.Id,
        week.Name,
        week.Competition,
        week.Fixtures.OrderBy(f => f.Kickoff).Select(ToDto)
    );

    public static string MapStatus(FixtureStatus s) => s switch
    {
        FixtureStatus.PreMatch => "pre_match",
        FixtureStatus.Live or FixtureStatus.HalfTime => "live",
        FixtureStatus.Ended => "ended",
        FixtureStatus.Postponed => "postponed",
        FixtureStatus.Cancelled => "cancelled",
        _ => s.ToString().ToLowerInvariant()
    };
}
