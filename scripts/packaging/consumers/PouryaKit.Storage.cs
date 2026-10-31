using PouryaKit.Domain;
using PouryaKit.Storage;

// A Feature's view of the packed contract: it names no provider, and a download that finds no file is a not-found Error.
System.Type[] types = [typeof(IFileStorage), typeof(StoredFile)];
Result<StoredFile> missing = StorageErrors.FileNotFound;
Console.WriteLine(
    missing.IsFailure && missing.Errors[0].Kind == ErrorKind.NotFound ? types.Length : 0
);
