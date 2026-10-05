using System.Data;
using System.Globalization;
using Dapper;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.Dapper;

// Passes a typed ID to the database as its value and reads a column back into the typed ID. One closed type per typed
// ID, registered by AddPouryaKitDapper; it holds no state, so one instance serves every Host in the process.
internal sealed class TypedIdTypeHandler<TId, TValue> : SqlMapper.TypeHandler<TId>
    where TId : TypedId<TValue>
    where TValue : notnull
{
    public override void SetValue(IDbDataParameter parameter, TId? value)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        parameter.Value = value is null ? DBNull.Value : value.Value;
    }

    // A column of another type, such as an integer column read into a long ID, is converted to the ID's value type.
    public override TId Parse(object value) =>
        TypedId.From<TId, TValue>(
            value is TValue typed ? typed : (TValue)Convert.ChangeType(value, typeof(TValue), CultureInfo.InvariantCulture)
        );
}
