using System.Text.Json;

namespace GrantPath.Abac;

/// <summary>How a leaf compares an attribute against a literal.</summary>
public enum ComparisonOperator
{
    /// <summary>The attribute is present, whatever its value.</summary>
    Exists = 0,

    /// <summary>Equal, comparing like with like.</summary>
    Equals = 1,

    /// <summary>Not equal. An absent attribute is not equal to anything.</summary>
    NotEquals = 2,

    /// <summary>The attribute appears in the given array.</summary>
    In = 3,

    /// <summary>The attribute does not appear in the given array.</summary>
    NotIn = 4,

    /// <summary>Numerically greater.</summary>
    GreaterThan = 5,

    /// <summary>Numerically greater or equal.</summary>
    GreaterThanOrEqual = 6,

    /// <summary>Numerically smaller.</summary>
    LessThan = 7,

    /// <summary>Numerically smaller or equal.</summary>
    LessThanOrEqual = 8,

    /// <summary>The attribute is an array containing the given value.</summary>
    Contains = 9,
}

/// <summary>
/// One node of a parsed policy condition.
/// </summary>
/// <remarks>
/// <para>
/// Policies are authored as JSON and parsed into this tree once, when the policy is loaded. Evaluating a
/// request then walks typed objects rather than re-reading a document, which is what keeps the evaluator
/// off the profile at request time.
/// </para>
/// <para>
/// An absent attribute never satisfies a comparison, including <see cref="ComparisonOperator.NotEquals"/>.
/// That looks asymmetric until you consider what the alternative means: a deny policy written as
/// "clearance is not secret" would fire for a subject that has no clearance attribute at all, which is
/// arguably right, and would equally fire because somebody misspelled the attribute name, which is not.
/// Missing data produces no opinion, and deny-by-default is enforced by the relationship layer rather than
/// by guessing here.
/// </para>
/// </remarks>
public abstract record ConditionNode
{
    /// <summary>Evaluates the node.</summary>
    /// <param name="attributes">The request's attributes.</param>
    public abstract bool Matches(AttributeSet attributes);

    /// <summary>Parses a condition document.</summary>
    /// <param name="json">The condition, as written in the policy.</param>
    /// <exception cref="FormatException">The document is not a condition this evaluator understands.</exception>
    public static ConditionNode Parse(JsonElement json)
    {
        if (json.ValueKind is not JsonValueKind.Object)
        {
            throw new FormatException("A condition must be a JSON object.");
        }

        if (json.TryGetProperty("allOf", out JsonElement all))
        {
            return new AllOfNode([.. ParseChildren(all, "allOf")]);
        }

        if (json.TryGetProperty("anyOf", out JsonElement any))
        {
            return new AnyOfNode([.. ParseChildren(any, "anyOf")]);
        }

        if (json.TryGetProperty("not", out JsonElement negated))
        {
            return new NotNode(Parse(negated));
        }

        return ComparisonNode.ParseLeaf(json);
    }

    private static IEnumerable<ConditionNode> ParseChildren(JsonElement array, string name)
    {
        if (array.ValueKind is not JsonValueKind.Array)
        {
            throw new FormatException($"'{name}' must be an array of conditions.");
        }

        foreach (JsonElement child in array.EnumerateArray())
        {
            yield return Parse(child);
        }
    }
}

/// <summary>Every child must match.</summary>
/// <param name="Children">The conditions.</param>
public sealed record AllOfNode(IReadOnlyList<ConditionNode> Children) : ConditionNode
{
    /// <inheritdoc />
    public override bool Matches(AttributeSet attributes)
    {
        // Indexed rather than foreach, and no LINQ, because this is the hot path and a policy set is
        // walked in full on every decision.
        for (int index = 0; index < Children.Count; index++)
        {
            if (!Children[index].Matches(attributes))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>At least one child must match. An empty list matches nothing.</summary>
/// <param name="Children">The conditions.</param>
public sealed record AnyOfNode(IReadOnlyList<ConditionNode> Children) : ConditionNode
{
    /// <inheritdoc />
    public override bool Matches(AttributeSet attributes)
    {
        for (int index = 0; index < Children.Count; index++)
        {
            if (Children[index].Matches(attributes))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Inverts a child.</summary>
/// <param name="Child">The condition.</param>
public sealed record NotNode(ConditionNode Child) : ConditionNode
{
    /// <inheritdoc />
    public override bool Matches(AttributeSet attributes) => !Child.Matches(attributes);
}

/// <summary>Compares one attribute against a literal.</summary>
/// <param name="Scope">Which side the attribute comes from.</param>
/// <param name="Name">Attribute name, without the scope prefix.</param>
/// <param name="Operator">How to compare.</param>
/// <param name="Value">The literal, as written in the policy.</param>
public sealed record ComparisonNode(
    AttributeScope Scope,
    string Name,
    ComparisonOperator Operator,
    JsonElement Value) : ConditionNode
{
    /// <inheritdoc />
    public override bool Matches(AttributeSet attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        if (!attributes.TryGet(Scope, Name, out JsonElement actual))
        {
            // Nothing to compare. See the note on ConditionNode: an attribute nobody supplied is not
            // evidence for anything, in either direction.
            return false;
        }

        return Operator switch
        {
            ComparisonOperator.Exists => true,
            ComparisonOperator.Equals => AreEqual(actual, Value),
            ComparisonOperator.NotEquals => !AreEqual(actual, Value),
            ComparisonOperator.In => Contains(Value, actual),
            ComparisonOperator.NotIn => !Contains(Value, actual),
            ComparisonOperator.Contains => Contains(actual, Value),
            _ => CompareNumbers(actual, Value, Operator),
        };
    }

    /// <summary>Parses a leaf comparison.</summary>
    /// <param name="json">The leaf document.</param>
    /// <exception cref="FormatException">A required field is missing or not understood.</exception>
    public static ComparisonNode ParseLeaf(JsonElement json)
    {
        if (!json.TryGetProperty("attribute", out JsonElement path)
            || path.ValueKind is not JsonValueKind.String)
        {
            throw new FormatException("A comparison needs an 'attribute' path.");
        }

        (AttributeScope scope, string name) = SplitPath(path.GetString()!);

        if (!json.TryGetProperty("operator", out JsonElement op) || op.ValueKind is not JsonValueKind.String)
        {
            throw new FormatException("A comparison needs an 'operator'.");
        }

        if (!Enum.TryParse(op.GetString(), ignoreCase: true, out ComparisonOperator parsed))
        {
            throw new FormatException($"'{op.GetString()}' is not an operator this evaluator knows.");
        }

        JsonElement value = json.TryGetProperty("value", out JsonElement literal)
            ? literal.Clone()
            : default;

        if (parsed is not ComparisonOperator.Exists && value.ValueKind is JsonValueKind.Undefined)
        {
            throw new FormatException($"Operator '{parsed}' needs a 'value'.");
        }

        return new ComparisonNode(scope, name, parsed, value);
    }

    private static (AttributeScope Scope, string Name) SplitPath(string path)
    {
        int separator = path.IndexOf('.', StringComparison.Ordinal);

        if (separator <= 0 || separator == path.Length - 1)
        {
            throw new FormatException(
                $"'{path}' is not an attribute path. Write it as subject.x, resource.x or environment.x.");
        }

        ReadOnlySpan<char> scope = path.AsSpan(0, separator);

        return (
            scope switch
            {
                "subject" => AttributeScope.Subject,
                "resource" => AttributeScope.Resource,
                "environment" => AttributeScope.Environment,
                _ => throw new FormatException(
                    $"'{scope}' is not a scope. Use subject, resource or environment."),
            },
            path[(separator + 1)..]);
    }

    private static bool AreEqual(JsonElement left, JsonElement right) =>
        (left.ValueKind, right.ValueKind) switch
        {
            (JsonValueKind.String, JsonValueKind.String) =>
                string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),

            (JsonValueKind.Number, JsonValueKind.Number) =>
                left.GetDouble().Equals(right.GetDouble()),

            (JsonValueKind.True, JsonValueKind.True) => true,
            (JsonValueKind.False, JsonValueKind.False) => true,
            _ => false,
        };

    private static bool Contains(JsonElement array, JsonElement value)
    {
        if (array.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement element in array.EnumerateArray())
        {
            if (AreEqual(element, value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CompareNumbers(JsonElement left, JsonElement right, ComparisonOperator op)
    {
        if (left.ValueKind is not JsonValueKind.Number || right.ValueKind is not JsonValueKind.Number)
        {
            return false;
        }

        int comparison = left.GetDouble().CompareTo(right.GetDouble());

        return op switch
        {
            ComparisonOperator.GreaterThan => comparison > 0,
            ComparisonOperator.GreaterThanOrEqual => comparison >= 0,
            ComparisonOperator.LessThan => comparison < 0,
            ComparisonOperator.LessThanOrEqual => comparison <= 0,
            _ => false,
        };
    }
}
