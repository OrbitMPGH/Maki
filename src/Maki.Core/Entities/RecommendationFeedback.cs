using Maki.Core.Security;

namespace Maki.Core.Entities;

public enum RecommendationSuppression { None, Hidden, Dismissed }

[Flags]
public enum RecommendationExposure { None = 0, Manga = 1, Anime = 2, Unspecified = 4 }

public class RecommendationFeedback : IUserOwned
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string Provider { get; set; } = "mangabaka";
    public long ProviderId { get; set; }
    public string? Title { get; set; }
    public RecommendationSuppression Suppression { get; set; }
    public DateTime? DismissedUntilUtc { get; set; }
    public RecommendationExposure Exposure { get; set; }
    public long Revision { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class RecommendationFeedbackEvent : IUserOwned
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public long ProviderId { get; set; }
    public string? Title { get; set; }
    public string Action { get; set; } = "";
    public string PreviousState { get; set; } = "";
    public string NewState { get; set; } = "";
    public long StateRevision { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public Guid ClientMutationId { get; set; }
}

public class RecommendationSignalOverride : IUserOwned
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string Provider { get; set; } = "mangabaka";
    public long ProviderId { get; set; }
    public bool IgnoreAsSeed { get; set; }
    public long Revision { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class RecommendationProfileState : IUserOwned
{
    public int UserId { get; set; }
    public long FeedbackRevision { get; set; }
    public long SignalRevision { get; set; }
}

public class RecommendationMutationReceipt : IUserOwned
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public Guid ClientMutationId { get; set; }
    public string Operation { get; set; } = "";
    public long ProviderId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}
