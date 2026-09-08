using System.Collections.Frozen;
using System.Text.Json;

namespace GrantPath.Abac;

/// <summary>Which side of the request an attribute came from.</summary>
public enum AttributeScope
{
    /// <summary>Something about the caller: clearance, department, employment status.</summary>
    Subject = 0,

    /// <summary>Something about the thing being reached for: classification, owner, retention class.</summary>
    Resource = 1,

    /// <summary>Something about the moment: time of day, source address, device posture.</summary>
    Environment = 2,
}

/// <summary>
/// The attributes a single decision is evaluated against.
/// </summary>
/// <remarks>
/// <para>
/// Three flat maps rather than one nested document. Attribute paths are resolved when a policy is parsed,
/// not when it is evaluated, so a comparison at request time is one dictionary lookup and no string
/// splitting. On a path that runs hundreds of times a second and has a five millisecond budget, that is
/// the difference between the evaluator being free and the evaluator being visible.
/// </para>
/// <para>
/// Values stay as <see cref="JsonElement"/> because that is how they arrive and how policies are written.
/// Converting them to a bespoke value type would mean deciding, at parse time, which of them are numbers,
/// and being wrong about that quietly.
/// </para>
/// </remarks>
public sealed class AttributeSet
{
    private static readonly FrozenDictionary<string, JsonElement> Empty =
        FrozenDictionary<string, JsonElement>.Empty;

    private readonly FrozenDictionary<string, JsonElement> _subject;
    private readonly FrozenDictionary<string, JsonElement> _resource;
    private readonly FrozenDictionary<string, JsonElement> _environment;

    /// <summary>Builds a set from three maps, any of which may be null.</summary>
    /// <param name="subject">Attributes of the caller.</param>
    /// <param name="resource">Attributes of the resource.</param>
    /// <param name="environment">Attributes of the request.</param>
    public AttributeSet(
        IReadOnlyDictionary<string, JsonElement>? subject = null,
        IReadOnlyDictionary<string, JsonElement>? resource = null,
        IReadOnlyDictionary<string, JsonElement>? environment = null)
    {
        _subject = Freeze(subject);
        _resource = Freeze(resource);
        _environment = Freeze(environment);
    }

    /// <summary>An empty set, which is what a request with no attributes evaluates against.</summary>
    public static AttributeSet None { get; } = new();

    /// <summary>Looks an attribute up.</summary>
    /// <param name="scope">Which side it belongs to.</param>
    /// <param name="name">Attribute name.</param>
    /// <param name="value">The value, when present.</param>
    public bool TryGet(AttributeScope scope, string name, out JsonElement value) =>
        (scope switch
        {
            AttributeScope.Subject => _subject,
            AttributeScope.Resource => _resource,
            _ => _environment,
        }).TryGetValue(name, out value);

    private static FrozenDictionary<string, JsonElement> Freeze(
        IReadOnlyDictionary<string, JsonElement>? source) =>
        source is null || source.Count is 0
            ? Empty
            : source.ToFrozenDictionary(StringComparer.Ordinal);
}
