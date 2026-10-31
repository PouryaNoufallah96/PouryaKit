using System.Runtime.CompilerServices;
using PouryaKit.Core.Tests.Lookup;

namespace PouryaKit.Core.Tests;

public class ImplementingTypesTests
{
    private static readonly System.Reflection.Assembly Fixtures = typeof(IHandler<>).Assembly;

    #region implementing-types-of
    [Fact]
    public void An_open_generic_definition_finds_each_concrete_class_that_closes_it_directly_or_through_a_base_class()
    {
        // The assembly is given twice and read once. Open generic, abstract, interface and struct implementations are
        // left out.
        Assert.Equal(
            [typeof(DerivedHandler), typeof(NoteHandler)],
            ImplementingTypes.Of(typeof(IHandler<>), Fixtures, Fixtures)
        );
        Assert.Equal([typeof(Ledger)], ImplementingTypes.Of(typeof(Store<>), Fixtures));
    }
    #endregion

    [Fact]
    public void A_non_generic_or_closed_contract_finds_each_concrete_class_assignable_to_it()
    {
        Assert.Equal(
            [typeof(DerivedHandler), typeof(NoteHandler)],
            ImplementingTypes.Of(typeof(IAudited), Fixtures)
        );
        Assert.Equal(
            [typeof(DerivedHandler)],
            ImplementingTypes.Of(typeof(IHandler<int>), Fixtures)
        );
    }

    [Fact]
    public void Candidates_include_the_abstract_and_open_generic_classes_that_of_leaves_out()
    {
        // Interface and struct implementations are still left out.
        Assert.Equal(
            [
                typeof(AbstractHandler),
                typeof(DerivedHandler),
                typeof(NoteHandler),
                typeof(OpenHandler<>),
            ],
            ImplementingTypes.Candidates(typeof(IHandler<>), Fixtures, Fixtures)
        );
        Assert.Equal(
            [typeof(AbstractStore), typeof(Ledger)],
            ImplementingTypes.Candidates(typeof(Store<>), Fixtures)
        );
        Assert.Equal(
            [
                typeof(AbstractHandler),
                typeof(DerivedHandler),
                typeof(NoteHandler),
                typeof(OpenHandler<>),
            ],
            ImplementingTypes.Candidates(typeof(IAudited), Fixtures)
        );
    }

    [Fact]
    public void Candidates_include_a_class_the_compiler_generated()
    {
        var iterator = Assert.Single(
            ImplementingTypes.Candidates(typeof(IEnumerable<NoteHandler>), Fixtures)
        );

        Assert.True(iterator.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false));
    }

    [Fact]
    public void A_contract_that_is_neither_closed_nor_a_generic_definition_is_rejected()
    {
        var parameter = typeof(IHandler<>).GetGenericArguments()[0];

        Assert.Throws<ArgumentException>(() => ImplementingTypes.Of(parameter, Fixtures));
        Assert.Throws<ArgumentException>(() =>
            ImplementingTypes.Of(typeof(IHandler<>).MakeGenericType(typeof(Store<>)), Fixtures)
        );
        Assert.Throws<ArgumentException>(() => ImplementingTypes.Candidates(parameter, Fixtures));
    }
}
