using PouryaKit.Core.Tests.Lookup;

namespace PouryaKit.Core.Tests;

public class ImplementingTypesTests
{
    private static readonly System.Reflection.Assembly Fixtures = typeof(IHandler<>).Assembly;

    [Fact]
    public void An_open_generic_definition_finds_each_concrete_class_that_closes_it_directly_or_through_a_base_class()
    {
        // The assembly is given twice and read once. Open generic, abstract, interface and struct implementations are
        // left out.
        Assert.Equal([typeof(DerivedHandler), typeof(NoteHandler)], ImplementingTypes.Of(typeof(IHandler<>), Fixtures, Fixtures));
        Assert.Equal([typeof(Ledger)], ImplementingTypes.Of(typeof(Store<>), Fixtures));
    }

    [Fact]
    public void A_non_generic_or_closed_contract_finds_each_concrete_class_assignable_to_it()
    {
        Assert.Equal([typeof(DerivedHandler), typeof(NoteHandler)], ImplementingTypes.Of(typeof(IAudited), Fixtures));
        Assert.Equal([typeof(DerivedHandler)], ImplementingTypes.Of(typeof(IHandler<int>), Fixtures));
    }

    [Fact]
    public void A_contract_that_is_neither_closed_nor_a_generic_definition_is_rejected()
    {
        var parameter = typeof(IHandler<>).GetGenericArguments()[0];

        Assert.Throws<ArgumentException>(() => ImplementingTypes.Of(parameter, Fixtures));
        Assert.Throws<ArgumentException>(() => ImplementingTypes.Of(typeof(IHandler<>).MakeGenericType(typeof(Store<>)), Fixtures));
    }
}
