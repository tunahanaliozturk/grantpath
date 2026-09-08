using GrantPath.Api;

// The whole entry point. Everything else is in GrantPathHost so the test suites can start the same
// application in process rather than a lookalike assembled from its parts.
await GrantPathHost.Build(args).RunAsync();
