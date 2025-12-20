using Xunit;

namespace MAS_BT.Tests.Collections;

public sealed class MessagingIntegrationFixture
{
}

[CollectionDefinition("MessagingIntegration", DisableParallelization = true)]
public sealed class MessagingIntegrationCollection : ICollectionFixture<MessagingIntegrationFixture>
{
}
