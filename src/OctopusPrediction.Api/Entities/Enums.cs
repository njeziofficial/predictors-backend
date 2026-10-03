namespace OctopusPrediction.Api.Entities;

public enum FixtureStatus
{
    PreMatch = 0,
    Live = 1,
    HalfTime = 2,
    Ended = 3,
    Postponed = 4,
    Cancelled = 5
}

public enum OutcomeType
{
    HomeWin = 0,
    AwayWin = 1,
    Draw = 2,
    CorrectScore = 3
}

public enum UserRole
{
    User = 0,
    Admin = 1
}
