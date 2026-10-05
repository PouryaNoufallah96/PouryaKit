using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

// Stores a typed ID as its value. One closed type per typed ID, created by PouryaKitDbContext's conventions.
internal sealed class TypedIdConverter<TId, TValue>()
    : ValueConverter<TId, TValue>(id => id.Value, value => TypedId.From<TId, TValue>(value))
    where TId : TypedId<TValue>
    where TValue : notnull;
