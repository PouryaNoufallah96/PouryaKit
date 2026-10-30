![PouryaKit.Testing](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Testing.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Testing?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Testing)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Testing

Test your application the way it runs. One `HostFixture` per test assembly builds the real Host with the same composition `Program` uses, starts PostgreSQL in a container, applies every Module's migrations and starts the application on it. Each test class derives from `HostTest<TFixture>`, which waits for the deliveries an earlier test caused and then empties every table, so no test sees another's data.

## Install

```bash
dotnet add package PouryaKit.Testing --prerelease
```

## When to use it

- You test a Feature against the hosted application, through its Module's Gateway or over HTTP.
- A test needs a particular Actor, tenant or correlation, or a fixed time.
- You wait for the deliveries an Operation caused and assert what they did, or read and replay a dead letter.
- You abandon a Host mid-delivery to test a crash.

It runs on xUnit v3 and Testcontainers, and hosts whatever your `Program` composes with [`PouryaKit.Modularity`](https://www.nuget.org/packages/PouryaKit.Modularity) and [`PouryaKit.Messaging.Wolverine.EFCore`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.EFCore).

## Quick start

Derive one fixture for the assembly, over your Host's composition, as the templates generate it:

```csharp
[assembly: AssemblyFixture(typeof(App))]

public sealed class App : HostFixture
{
    protected override IHost CreateHost(string connectionString) => ComposeHost(Environments.Development, connectionString);
}
```

Send Operations through the Gateway, and assert Results and rows, as the Kit's tests do:

```csharp
[Fact]
public async Task A_category_counts_a_new_product_in_the_same_commit_and_a_failed_count_leaves_no_product()
{
    var catalog = Fixture.Gateway<CatalogModule>();
    var lamps = Guid.CreateVersion7();
    await catalog.SendAsync(new CreateCategoryCommand(lamps, "Lamps"), TestContext.Current.CancellationToken);

    var created = await catalog.SendAsync(
        new CreateProductCommand(Guid.CreateVersion7(), lamps, "Desk lamp"),
        TestContext.Current.CancellationToken
    );
    var refused = await catalog.SendAsync(
        new CreateProductCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), "Floor lamp"),
        TestContext.Current.CancellationToken
    );

    Assert.True(created.IsSuccess);
    Assert.Equal(CategoryErrors.NotFound, Assert.Single(refused.Errors));
    Assert.Equal([[lamps, 1]], await Fixture.RowsAsync("select id, product_count from catalog.categories"));
    Assert.Equal(["Desk lamp"], (await Fixture.RowsAsync("select name from catalog.products")).Select(row => row[0]));
}
```

## What you get

- The real Host, composed as `Program` composes it, on real PostgreSQL.
- Every table emptied between tests.
- Deliveries awaited, and dead letters read and replayed.
- A fixed clock, and a chosen Actor and tenant for an Operation.

> [!WARNING]
> `HostFixture` runs PostgreSQL in a Testcontainers container, so the machine that runs the tests needs Docker. Tests that share a fixture run one at a time.

## Learn more

- [Testing](https://pouryakit.vercel.app/docs/capabilities/testing)
- [Test a Feature](https://pouryakit.vercel.app/docs/guides/test-a-feature)
- [Deliveries](https://pouryakit.vercel.app/docs/capabilities/testing#deliveries)
- [Structure tests](https://pouryakit.vercel.app/docs/capabilities/testing#structure-tests)
