namespace OctopusPrediction.Api.Dtos.Admin;

// How players may predict; see ScraperSettings for what each rule means. Also the body of
// PUT /api/admin/prediction-rules, and part of GET /api/predictions/lock-status for players.
public record PredictionRulesDto(
    bool AllowPartialPredictions,
    bool PredictionsFinal,
    bool LockWeekAtFirstKickoff,
    bool AllowLatePredictions
);
