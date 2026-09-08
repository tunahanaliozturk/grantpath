using System.Reflection;
using System.Text.RegularExpressions;
using GrantPath.Api.Authorization;
using GrantPath.Rebac;
using OpenFga.Sdk.Model;
using RelationshipModel = GrantPath.Rebac.AuthorizationModel;

namespace GrantPath.UnitTests;

/// <summary>
/// Keeps the readable model and the running model from drifting apart.
/// </summary>
/// <remarks>
/// <para>
/// The model that runs is built in typed C#; the model people read is the DSL file under <c>model/</c>.
/// Two descriptions of the same thing drift, and the way they drift is that someone adds a relation in
/// code and the documentation quietly describes a system that no longer exists.
/// </para>
/// <para>
/// This does not parse the DSL properly, and it does not need to. It extracts type and relation names,
/// which is exactly the drift that matters: a relation present in one and absent from the other.
/// </para>
/// </remarks>
public sealed class AuthorizationModelTests
{
    private static readonly Regex TypePattern = new(
        @"^type\s+([a-z_]+)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex RelationPattern = new(
        @"^\s+define\s+([a-z_]+)\s*:",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    [Fact]
    public void The_documented_types_are_exactly_the_types_that_run()
    {
        string dsl = ReadDsl();

        string[] documented = [.. TypePattern.Matches(dsl).Select(match => match.Groups[1].Value)];
        string[] built = [.. RelationshipModel.Build().Select(definition => definition.Type)];

        documented.ShouldBe(built, ignoreOrder: true);
    }

    [Fact]
    public void The_documented_relations_are_exactly_the_relations_that_run()
    {
        string dsl = ReadDsl();

        HashSet<string> documented =
        [
            .. RelationPattern.Matches(dsl).Select(match => match.Groups[1].Value),
        ];

        HashSet<string> built = [];

        foreach (TypeDefinition definition in RelationshipModel.Build())
        {
            if (definition.Relations is null)
            {
                continue;
            }

            foreach (string relation in definition.Relations.Keys)
            {
                built.Add(relation);
            }
        }

        documented.ShouldBe(built, ignoreOrder: true);
    }

    [Fact]
    public void A_document_inherits_from_its_folder_and_can_be_blocked_on_it()
    {
        TypeDefinition document = RelationshipModel.Build()
            .Single(definition => definition.Type == RelationshipModel.Types.Document);

        Userset viewer = document.Relations![RelationshipModel.Relations.Viewer];

        // The one narrowing rule in the domain. A relationship graph only ever grants, so "explicitly
        // excluded" has to be a subtraction, and losing it would silently widen access.
        Difference difference = viewer.Difference.ShouldNotBeNull();
        difference.Subtract.ComputedUserset!.Relation.ShouldBe(RelationshipModel.Relations.Blocked);

        Usersets inherited = difference.Base.Union.ShouldNotBeNull();

        inherited.Child
            .Where(child => child.TupleToUserset is not null)
            .Select(child => child.TupleToUserset!.Tupleset.Relation)
            .ShouldContain(RelationshipModel.Relations.Parent);
    }

    [Theory]
    [InlineData("org", "viewer", "viewer")]
    [InlineData("org", "editor", "admin")]
    [InlineData("workspace", "owner", "admin")]
    [InlineData("folder", "editor", "editor")]
    [InlineData("document", "viewer", "viewer")]
    public void A_relation_is_asked_about_by_the_name_each_level_uses(
        string resourceType,
        string requested,
        string expected)
    {
        // Someone who may edit a document is an admin of the org above it, not an editor of it. Without
        // this mapping an explanation would ask the org the wrong question, get a deny, and report that
        // the grant came from nowhere.
        AuthorizationFacade.RelationAtLevel(resourceType, requested).ShouldBe(expected);
    }

    [Theory]
    [InlineData("document:handbook", "document")]
    [InlineData("org:acme", "org")]
    [InlineData("platform:grantpath", "platform")]
    public void A_resource_identifier_splits_into_a_type_and_an_id(string resource, string expected) =>
        AuthorizationFacade.TypeOf(resource).ShouldBe(expected);

    private static string ReadDsl()
    {
        Assembly assembly = typeof(AuthorizationModelTests).Assembly;

        string name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith("authorization-model.fga", StringComparison.Ordinal));

        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
