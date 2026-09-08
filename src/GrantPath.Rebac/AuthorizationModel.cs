using OpenFga.Sdk.Model;

namespace GrantPath.Rebac;

/// <summary>
/// The relationship model, built in typed objects rather than parsed from the DSL.
/// </summary>
/// <remarks>
/// <para>
/// OpenFGA's DSL is the readable form and lives at <c>model/authorization-model.fga</c>, but no .NET
/// parser for it ships with the SDK, so loading the DSL at runtime would mean either shelling out to the
/// OpenFGA CLI or writing a parser. Building the model here in typed objects means the compiler checks it
/// and a relation cannot be misspelled into existence.
/// </para>
/// <para>
/// The DSL file is therefore documentation, and documentation drifts. A test parses it and asserts that
/// every type and relation named there exists here, and that nothing exists here that the DSL does not
/// mention. That is the drift worth catching: a relation added in code and never explained.
/// </para>
/// </remarks>
public static class AuthorizationModel
{
    /// <summary>Types in the modelled domain.</summary>
    public static class Types
    {
        /// <summary>A person.</summary>
        public const string User = "user";

        /// <summary>The service itself, so administering it is an authorization decision like any other.</summary>
        public const string Platform = "platform";

        /// <summary>The top of the containment hierarchy.</summary>
        public const string Org = "org";

        /// <summary>Belongs to an org.</summary>
        public const string Workspace = "workspace";

        /// <summary>Belongs to a workspace.</summary>
        public const string Folder = "folder";

        /// <summary>Belongs to a folder. The leaf, four levels down from an org.</summary>
        public const string Document = "document";
    }

    /// <summary>Relations used across the model.</summary>
    public static class Relations
    {
        /// <summary>Containment. The relation the inheritance rules walk.</summary>
        public const string Parent = "parent";

        /// <summary>Belongs to this org or workspace.</summary>
        public const string Member = "member";

        /// <summary>Runs this org or workspace.</summary>
        public const string Admin = "admin";

        /// <summary>May read.</summary>
        public const string Viewer = "viewer";

        /// <summary>May change.</summary>
        public const string Editor = "editor";

        /// <summary>May do anything, including granting.</summary>
        public const string Owner = "owner";

        /// <summary>Explicitly excluded from this document, whatever the hierarchy says.</summary>
        public const string Blocked = "blocked";

        /// <summary>May administer this service.</summary>
        public const string Administrator = "administrator";
    }

    /// <summary>The single platform object every administrative check is made against.</summary>
    public const string PlatformObject = $"{Types.Platform}:grantpath";

    /// <summary>Builds the model exactly as it is written to the store.</summary>
    public static List<TypeDefinition> Build() =>
    [
        new TypeDefinition { Type = Types.User },

        new TypeDefinition
        {
            Type = Types.Platform,
            Relations = new Dictionary<string, Userset>(StringComparer.Ordinal)
            {
                [Relations.Administrator] = Direct(),
            },
            Metadata = MetadataFor((Relations.Administrator, [Types.User])),
        },

        new TypeDefinition
        {
            Type = Types.Org,
            Relations = new Dictionary<string, Userset>(StringComparer.Ordinal)
            {
                [Relations.Member] = Direct(),
                [Relations.Admin] = Direct(),

                // An org admin can see everything the members can. Spelling this out here rather than at
                // every level below is what stops the hierarchy needing a tuple per role per level.
                [Relations.Viewer] = Union(Computed(Relations.Member), Computed(Relations.Admin)),
            },
            Metadata = MetadataFor(
                (Relations.Member, [Types.User]),
                (Relations.Admin, [Types.User])),
        },

        new TypeDefinition
        {
            Type = Types.Workspace,
            Relations = new Dictionary<string, Userset>(StringComparer.Ordinal)
            {
                [Relations.Parent] = Direct(),
                [Relations.Member] = Union(Direct(), FromParent(Relations.Member)),
                [Relations.Admin] = Union(Direct(), FromParent(Relations.Admin)),
                [Relations.Viewer] = Union(Computed(Relations.Member), Computed(Relations.Admin)),
            },
            Metadata = MetadataFor(
                (Relations.Parent, [Types.Org]),
                (Relations.Member, [Types.User]),
                (Relations.Admin, [Types.User])),
        },

        new TypeDefinition
        {
            Type = Types.Folder,
            Relations = new Dictionary<string, Userset>(StringComparer.Ordinal)
            {
                [Relations.Parent] = Direct(),
                [Relations.Viewer] = Union(Direct(), FromParent(Relations.Viewer)),
                [Relations.Editor] = Union(Direct(), FromParent(Relations.Admin)),
                [Relations.Owner] = Union(Direct(), FromParent(Relations.Admin)),
            },
            Metadata = MetadataFor(
                (Relations.Parent, [Types.Workspace]),
                (Relations.Viewer, [Types.User]),
                (Relations.Editor, [Types.User]),
                (Relations.Owner, [Types.User])),
        },

        new TypeDefinition
        {
            Type = Types.Document,
            Relations = new Dictionary<string, Userset>(StringComparer.Ordinal)
            {
                [Relations.Parent] = Direct(),
                [Relations.Blocked] = Direct(),

                // Inheritance minus an explicit block. A relationship graph only ever adds access, so the
                // spec's "unless the document carries a more restrictive direct tuple" has to be written
                // as a subtraction; there is no other way to express a narrowing relationship.
                [Relations.Viewer] = Except(
                    Union(Direct(), FromParent(Relations.Viewer)),
                    Relations.Blocked),
                [Relations.Editor] = Except(
                    Union(Direct(), FromParent(Relations.Editor)),
                    Relations.Blocked),
                [Relations.Owner] = Except(
                    Union(Direct(), FromParent(Relations.Owner)),
                    Relations.Blocked),
            },
            Metadata = MetadataFor(
                (Relations.Parent, [Types.Folder]),
                (Relations.Blocked, [Types.User]),
                (Relations.Viewer, [Types.User]),
                (Relations.Editor, [Types.User]),
                (Relations.Owner, [Types.User])),
        },
    ];

    private static Userset Direct() => new() { This = new object() };

    private static Userset Computed(string relation) =>
        new() { ComputedUserset = new ObjectRelation { Relation = relation } };

    private static Userset FromParent(string relation) =>
        new()
        {
            TupleToUserset = new TupleToUserset
            {
                Tupleset = new ObjectRelation { Relation = Relations.Parent },
                ComputedUserset = new ObjectRelation { Relation = relation },
            },
        };

    private static Userset Union(params Userset[] children) =>
        new() { Union = new Usersets { Child = [.. children] } };

    private static Userset Except(Userset baseSet, string subtractedRelation) =>
        new()
        {
            Difference = new Difference
            {
                Base = baseSet,
                Subtract = Computed(subtractedRelation),
            },
        };

    private static Metadata MetadataFor(params (string Relation, string[] Types)[] relations)
    {
        var map = new Dictionary<string, RelationMetadata>(StringComparer.Ordinal);

        foreach ((string relation, string[] types) in relations)
        {
            map[relation] = new RelationMetadata
            {
                DirectlyRelatedUserTypes = [.. types.Select(type => new RelationReference { Type = type })],
            };
        }

        return new Metadata { Relations = map };
    }
}
