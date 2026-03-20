namespace TripletexAgent.Models;

/// <summary>
/// Result from a task handler, capturing created/modified entity IDs for post-task validation.
/// </summary>
public class HandlerResult
{
    public string EntityType { get; set; } = "";
    public long? EntityId { get; set; }

    /// <summary>Extra IDs (e.g. employment ID, order ID, invoice ID in a chain).</summary>
    public Dictionary<string, long> ExtraIds { get; set; } = new();

    /// <summary>Extra string metadata (e.g. whether admin role was assigned).</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>For multi-entity tasks (e.g. create multiple customers), additional entity IDs.</summary>
    public List<long> AdditionalEntityIds { get; set; } = new();

    public static HandlerResult Empty => new();
}
