namespace Kaimo_File_Server.Infrastructure;

/// <summary>The database schema is newer than this build; the process must not start.</summary>
public sealed class SchemaVersionMismatchException(string message) : InvalidOperationException(message);
