using OctopusPrediction.Api.Dtos.Fixtures;

namespace OctopusPrediction.Api.Dtos.MatchWeeks;

public record MatchWeekDto(
    string Id,
    string Name,
    string Competition,
    string CompetitionId,
    IEnumerable<FixtureDto> Fixtures
);
