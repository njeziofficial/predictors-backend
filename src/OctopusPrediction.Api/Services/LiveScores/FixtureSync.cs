using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services.Scraping;
using static OctopusPrediction.Api.Services.LiveScores.MatchReading;

namespace OctopusPrediction.Api.Services.LiveScores;

// Applies scraped matches to Fixtures (and new MatchWeeks), then scores the predictions of any
// fixture whose result changed. Run by MatchUpdateProcessor on the rows the scraper queued.
public sealed class FixtureSync(AppDbContext db, IScoringService scoring, ILogger<FixtureSync> logger)
{
    // A match can't be over sooner than this after kickoff (two halves and the break), so a
    // "finished" reading before then is a source glitch, not a result.
    private static readonly TimeSpan MinMatchDuration = TimeSpan.FromMinutes(100);

    private readonly HashSet<string> _addedWeeks = [];

    /// <summary>
    /// Saves the matches. Returns the fixtures whose reading wasn't taken as it stands because
    /// of the time (a "finished" too soon after kickoff): the same reading may be right on a
    /// later poll, so it must not be skipped as unchanged.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> SyncAsync(
        IReadOnlyList<ScrapedMatchDto> matches, string competition, CancellationToken ct)
    {
        var toScore = new List<string>();
        var heldBack = new HashSet<string>();

        // Other competitions in kickoff order, so a two-legged tie's 1st leg is filed before its
        // 2nd (see LegAsync). La Liga keeps the order the source gave.
        if (Competitions.IdFor(competition) != Competitions.LaLiga)
            matches = [.. matches.OrderBy(m => ParseKickoff(m.Time)?.Utc ?? DateTime.MaxValue)];

        foreach (var m in matches)
        {
            var (changedId, held) = await SyncMatchAsync(m, competition, ct);
            if (changedId is not null) toScore.Add(changedId);
            if (held) heldBack.Add(FixtureId(competition, m.Home, m.Away));
        }

        await db.SaveChangesAsync(ct);
        await RemoveEmptyUnknownWeekAsync(competition, ct);

        foreach (var fId in toScore)
        {
            await scoring.ScoreFixtureAsync(fId);
            logger.LogInformation("[LiveScores] Predictions scored for fixture {Id}", fId);
        }
        return heldBack;
    }

    // ChangedId is the fixtureId when its result changed (it ended, its final score changed, or it
    // stopped being ended), so its predictions are scored again. HeldBack: see SyncAsync.
    private async Task<(string? ChangedId, bool HeldBack)> SyncMatchAsync(
        ScrapedMatchDto m, string competition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(m.Home) || string.IsNullOrWhiteSpace(m.Away)) return (null, false);

        var fixtureId = FixtureId(competition, m.Home, m.Away);
        var hasRound = !string.IsNullOrWhiteSpace(m.Round);
        var laLiga = Competitions.IdFor(competition) == Competitions.LaLiga;
        var newStatus = MapStatus(m);
        var heldBack = false;

        var fixture = await db.Fixtures.FindAsync([fixtureId], ct);
        var wasEnded = fixture?.Status == FixtureStatus.Ended;
        var (oldFinalHome, oldFinalAway) = (fixture?.FinalScoreHome, fixture?.FinalScoreAway);

        // A bare "HH:mm" is only a guess at the date (see ParseKickoff), so it's the last resort.
        var scrapedKickoff = ParseKickoff(m.Time);
        var datedKickoff = scrapedKickoff is { HasDate: true } k ? k.Utc : (DateTime?)null;

        // Flashscore marks a row finished by the absence of its "live" marker, which can drop
        // out for a moment mid-match. Scoring that would award points for a half-time score.
        var kickoffForCheck = datedKickoff ?? fixture?.Kickoff ?? scrapedKickoff?.Utc;
        if (newStatus == FixtureStatus.Ended && kickoffForCheck is { } ko && DateTime.UtcNow < ko + MinMatchDuration)
        {
            var keep = DateTime.UtcNow < ko ? FixtureStatus.PreMatch
                : fixture?.Status is FixtureStatus.HalfTime ? FixtureStatus.HalfTime
                : FixtureStatus.Live;
            logger.LogWarning("[LiveScores] {Home} vs {Away} reported finished {Minutes:0} min after kickoff — treating it as {Status}",
                m.Home, m.Away, (DateTime.UtcNow - ko).TotalMinutes, keep);
            newStatus = keep;
            heldBack = true;
        }

        // Without a round there's no week to put a new fixture in: leave it until a poll finds
        // the round (FlashscoreSource looks it up), rather than invent a "Week ?".
        if (fixture is null && !hasRound)
        {
            logger.LogDebug("[LiveScores] Skipping new fixture {Home} vs {Away} until its round is known", m.Home, m.Away);
            return (null, heldBack);
        }

        // Only a new fixture, or one still in "Week ?", needs its week. Null for a round that
        // isn't predicted (Champions League qualifying) or can't be read.
        var week = hasRound && (fixture is null || fixture.WeekId == UnknownWeekId(competition))
            ? await WeekForAsync(m, competition, datedKickoff ?? scrapedKickoff?.Utc, ct)
            : null;
        var weekId = week?.Id ?? "";

        if (fixture is null && week is null)
        {
            logger.LogDebug("[LiveScores] Skipping {Home} vs {Away}: round {Round} isn't one players predict",
                m.Home, m.Away, m.Round);
            return (null, heldBack);
        }

        // A team plays once a round. If the week already has a match for either team, this is
        // that match under a spelling TeamNameNormalizer doesn't know yet: don't add it twice.
        if (fixture is null && await FindSameRoundMatchAsync(weekId, m, ct) is { } existing)
        {
            logger.LogWarning("[LiveScores] Not adding {Home} vs {Away}: {Week} already has {ExistingHome} vs {ExistingAway}. " +
                "If they're the same match, add the spelling to TeamNameNormalizer.",
                m.Home, m.Away, weekId, existing.HomeTeam, existing.AwayTeam);
            return (null, heldBack);
        }

        // Once a match has started or finished it never goes back to "not started". Sources that
        // only mark "FT" (ESPN, WorldFootball) show a match in play as not started, which would
        // otherwise reopen it and take its points away.
        if (fixture is not null && newStatus == FixtureStatus.PreMatch
            && fixture.Status is FixtureStatus.Live or FixtureStatus.HalfTime or FixtureStatus.Ended)
        {
            logger.LogDebug("[LiveScores] Ignoring 'not started' for {Home} vs {Away}, already {Status}",
                m.Home, m.Away, fixture.Status);
            return (null, heldBack);
        }

        if (week is { } w)
            await EnsureWeekAsync(w.Id, w.Name, competition, ct);

        if (fixture is not null && week is not null && fixture.WeekId == UnknownWeekId(competition))
            await MoveOutOfUnknownWeekAsync(fixture, weekId, ct);

        if (fixture is null)
        {
            fixture = new Fixture
            {
                Id = fixtureId,
                WeekId = weekId,
                HomeTeam = m.Home,
                AwayTeam = m.Away,
                Kickoff = scrapedKickoff?.Utc ?? DateTime.UtcNow,
                Status = newStatus,
                UpdatedAt = DateTime.UtcNow
            };
            db.Fixtures.Add(fixture);
            logger.LogDebug("[LiveScores] New fixture: {Home} vs {Away} [{Id}]", m.Home, m.Away, fixtureId);
        }
        else
        {
            fixture.Status = newStatus;
            fixture.UpdatedAt = DateTime.UtcNow;
            // Only a time that comes with its date may move a kickoff, which drives prediction
            // locks: a bare "HH:mm" may be another day's match, or in the site's own time zone.
            if (datedKickoff.HasValue) fixture.Kickoff = datedKickoff.Value;
        }

        var scoreHome = ParseScore(m.ScoreHome);
        var scoreAway = ParseScore(m.ScoreAway);

        if (newStatus == FixtureStatus.Ended)
        {
            if (laLiga || !EndAfterNinetyMinutes(fixture, m, scoreHome, scoreAway))
            {
                fixture.FinalScoreHome = scoreHome;
                fixture.FinalScoreAway = scoreAway;
            }
            fixture.LiveScoreHome = null;
            fixture.LiveScoreAway = null;
            fixture.LiveMinute = null;
        }
        else if (newStatus is FixtureStatus.Live or FixtureStatus.HalfTime)
        {
            fixture.LiveScoreHome = scoreHome;
            fixture.LiveScoreAway = scoreAway;
            fixture.LiveMinute = ParseMinute(m.Stage);
            // Back in play after a wrong "finished" reading: that result no longer stands.
            fixture.FinalScoreHome = null;
            fixture.FinalScoreAway = null;
            if (!laLiga) TrackNormalTime(fixture, m, scoreHome, scoreAway);
        }

        var isEnded = newStatus == FixtureStatus.Ended;
        var resultChanged = isEnded != wasEnded
            || (isEnded && (fixture.FinalScoreHome != oldFinalHome || fixture.FinalScoreAway != oldFinalAway));
        return (resultChanged ? fixtureId : null, heldBack);
    }

    // A fixture in the week (saved, or added earlier this batch) involving either of m's teams.
    private async Task<Fixture?> FindSameRoundMatchAsync(string weekId, ScrapedMatchDto m, CancellationToken ct)
    {
        string[] teams = [TeamNameNormalizer.Slug(m.Home), TeamNameNormalizer.Slug(m.Away)];
        var inWeek = await db.Fixtures.Where(f => f.WeekId == weekId).ToListAsync(ct);
        return inWeek.Concat(db.Fixtures.Local.Where(f => f.WeekId == weekId))
            .FirstOrDefault(f => teams.Contains(TeamNameNormalizer.Slug(f.HomeTeam))
                || teams.Contains(TeamNameNormalizer.Slug(f.AwayTeam)));
    }

    // The week a match with a round belongs in. La Liga: "la-liga-week-8", as always. The
    // Champions League: a matchday, or one leg of a knockout round; null for qualifying.
    private async Task<(string Id, string Name)?> WeekForAsync(
        ScrapedMatchDto m, string competition, DateTime? kickoff, CancellationToken ct)
    {
        var competitionId = Competitions.IdFor(competition);
        if (competitionId != Competitions.ChampionsLeague)
            return (WeekId(competition, m.Round), WeekName(competition, m.Round));

        if (CompetitionRounds.ChampionsLeague(m.Round, kickoff) is not { } round) return null;
        var leg = round.TwoLegged ? await LegAsync(competition, round, m, kickoff, ct) : 1;
        var (key, name) = CompetitionRounds.Leg(round, leg);
        return (CompetitionRounds.WeekId(competitionId, key), $"{competition} — {name}");
    }

    // Sources name the round ("Round of 16"), not the leg. The 2nd leg is the reverse fixture of
    // a 1st leg already filed in that round, played after it (matches are synced in kickoff order).
    private async Task<int> LegAsync(
        string competition, CompetitionRounds.Round round, ScrapedMatchDto m, DateTime? kickoff, CancellationToken ct)
    {
        var firstLegWeek = CompetitionRounds.WeekId(Competitions.IdFor(competition), CompetitionRounds.Leg(round, 1).Key);
        var reverse = await db.Fixtures.FindAsync([FixtureId(competition, m.Away, m.Home)], ct);
        return reverse is not null && reverse.WeekId == firstLegWeek && (kickoff is null || reverse.Kickoff < kickoff) ? 2 : 1;
    }

    // Knockout matches that can go to extra time: the last score seen in normal time is kept
    // (stoppage time and the break before extra time count; extra time doesn't).
    private static void TrackNormalTime(Fixture f, ScrapedMatchDto m, int? home, int? away)
    {
        if (!CompetitionRounds.CanGoToExtraTime(f.WeekId)) return;
        if (IsExtraTime(m.Stage)) f.WentToExtraTime = true;
        else if (!f.WentToExtraTime) (f.RegularTimeScoreHome, f.RegularTimeScoreAway) = (home, away);
    }

    // Predictions are marked on the score after 90 minutes. Returns true when the final score
    // has been dealt with here (set to the 90-minute score, or left for an admin), false when
    // the scraped score stands as it is: a match that can't go to extra time, or didn't.
    private bool EndAfterNinetyMinutes(Fixture f, ScrapedMatchDto m, int? home, int? away)
    {
        if (!CompetitionRounds.CanGoToExtraTime(f.WeekId)) return false;

        if (f.WentToExtraTime || IsExtraTime(m.Stage))
        {
            f.WentToExtraTime = true;
            (f.AfterExtraTimeHome, f.AfterExtraTimeAway) = (home, away);
            if (f.RegularTimeScoreHome is not null)
                (f.FinalScoreHome, f.FinalScoreAway) = (f.RegularTimeScoreHome, f.RegularTimeScoreAway);
            else
                logger.LogWarning("[LiveScores] {Home} vs {Away} went to extra time but its 90-minute score wasn't seen. " +
                    "Enter it on the admin fixtures page.", f.HomeTeam, f.AwayTeam);
            return true;
        }

        // Never seen live (the backend was asleep): a final reading can't tell a 90-minute result
        // from one after extra time, so it isn't trusted. An admin's score is left as it is.
        if (f.RegularTimeScoreHome is null)
        {
            logger.LogWarning("[LiveScores] {Home} vs {Away} finished {Score} without being seen live: it may have gone to " +
                "extra time. Enter its 90-minute score on the admin fixtures page.", f.HomeTeam, f.AwayTeam, $"{home}-{away}");
            return true;
        }
        return false;
    }

    private async Task EnsureWeekAsync(string weekId, string weekName, string competition, CancellationToken ct)
    {
        if (_addedWeeks.Contains(weekId) || await db.MatchWeeks.AnyAsync(w => w.Id == weekId, ct)) return;
        db.MatchWeeks.Add(new MatchWeek
        {
            Id = weekId,
            Name = weekName,
            Competition = competition,
            CompetitionId = Competitions.IdFor(competition)
        });
        _addedWeeks.Add(weekId);
        logger.LogInformation("[LiveScores] Created MatchWeek: {Id}", weekId);
    }

    // Older scrapes filed fixtures whose round wasn't on the page under "Week ?". Once the round
    // is known, move the fixture and its predictions to the right week. Tracked changes, not a
    // bulk update, so the save empties the cached weeks, predictions and leaderboard.
    private async Task MoveOutOfUnknownWeekAsync(Fixture fixture, string weekId, CancellationToken ct)
    {
        logger.LogInformation("[LiveScores] Moving {Home} vs {Away} from {From} to {To}",
            fixture.HomeTeam, fixture.AwayTeam, fixture.WeekId, weekId);
        fixture.WeekId = weekId;
        foreach (var prediction in await db.Predictions.Where(p => p.FixtureId == fixture.Id).ToListAsync(ct))
            prediction.WeekId = weekId;
    }

    // Drops "Week ?" once every fixture has been moved out of it (see MoveOutOfUnknownWeekAsync).
    private async Task RemoveEmptyUnknownWeekAsync(string competition, CancellationToken ct)
    {
        var unknownWeekId = UnknownWeekId(competition);
        var week = await db.MatchWeeks.FindAsync([unknownWeekId], ct);
        if (week is null || await db.Fixtures.AnyAsync(f => f.WeekId == unknownWeekId, ct)) return;
        db.MatchWeeks.Remove(week);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("[LiveScores] Removed empty MatchWeek {Id}", unknownWeekId);
    }
}
