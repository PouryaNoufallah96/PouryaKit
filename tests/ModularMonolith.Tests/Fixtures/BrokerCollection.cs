namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// The tests of delivery between Hosts, on one <see cref="BrokerFixture"/>: a run starts one broker and one PostgreSQL
/// server for all of them, and each test still gets a vhost and databases of its own. They run one at a time, beside
/// no other test, as each pair of Hosts opens pools of its own.
/// </summary>
[CollectionDefinition(DisableParallelization = true)]
public sealed class BrokerCollection : ICollectionFixture<BrokerFixture>;
