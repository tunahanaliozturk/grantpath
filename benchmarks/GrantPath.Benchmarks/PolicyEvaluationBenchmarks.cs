using System.Text.Json;
using BenchmarkDotNet.Attributes;
using GrantPath.Abac;

namespace GrantPath.Benchmarks;

/// <summary>
/// What the attribute layer costs, with the network taken out of the picture.
/// </summary>
/// <remarks>
/// <para>
/// A whole decision is dominated by a round trip to the relationship store, which makes it useless for
/// answering the question this benchmark exists for: whether the part written by hand is a rounding error
/// or a problem. Measuring it in isolation is the only way to see it at all.
/// </para>
/// <para>
/// The policy set is walked in full on every decision, because a deny at any priority has to be able to
/// outrank an allow, so the cost grows with the number of policies for a resource type rather than with
/// the number that match.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class PolicyEvaluationBenchmarks
{
    private const string Simple =
        """{ "attribute": "resource.classification", "operator": "equals", "value": "confidential" }""";

    private const string Nested = """
        {
          "allOf": [
            { "attribute": "resource.classification", "operator": "in", "value": ["confidential", "secret"] },
            { "not": { "attribute": "subject.clearance", "operator": "in", "value": ["secret", "top-secret"] } },
            {
              "anyOf": [
                { "attribute": "subject.department", "operator": "equals", "value": "engineering" },
                { "attribute": "subject.years", "operator": "greaterThanOrEqual", "value": 5 }
              ]
            }
          ]
        }
        """;

    private AbacPolicy[] _policies = [];
    private AttributeSet _attributes = AttributeSet.None;

    /// <summary>How many policies exist for the resource type being decided.</summary>
    [Params(1, 8, 64)]
    public int PolicyCount { get; set; }

    /// <summary>Whether the conditions are single comparisons or nested trees.</summary>
    [Params(false, true)]
    public bool Nesting { get; set; }

    /// <summary>Compiles the policy set once, as the service does when it loads them.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string condition = Nesting ? Nested : Simple;

        _policies =
        [
            .. Enumerable.Range(0, PolicyCount).Select(index => AbacPolicy.Compile(
                Guid.CreateVersion7(),
                $"policy-{index}",
                "document",
                index % 2 is 0 ? PolicyEffect.Deny : PolicyEffect.Allow,
                index,
                condition)),
        ];

        _attributes = new AttributeSet(
            subject: Read("""{ "clearance": "public", "department": "engineering", "years": 7 }"""),
            resource: Read("""{ "classification": "confidential" }"""));
    }

    /// <summary>Evaluates the whole policy set against one request.</summary>
    [Benchmark]
    public AbacVerdict Evaluate() => AbacEvaluator.Evaluate(_policies, _attributes).Verdict;

    private static Dictionary<string, JsonElement> Read(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }
}
