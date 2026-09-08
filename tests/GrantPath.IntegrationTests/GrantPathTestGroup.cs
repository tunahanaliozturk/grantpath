using GrantPath.TestSupport;

namespace GrantPath.IntegrationTests;

/// <summary>Shares one service, one database and one relationship store across the suite.</summary>
[CollectionDefinition(Name)]
public sealed class GrantPathTestGroup : ICollectionFixture<GrantPathFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "grantpath";
}
