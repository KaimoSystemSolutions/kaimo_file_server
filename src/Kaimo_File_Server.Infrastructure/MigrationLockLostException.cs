namespace Kaimo_File_Server.Infrastructure;

/// <summary>The session holding the migration lock was lost; startup retries the sequence.</summary>
public sealed class MigrationLockLostException(string message) : Exception(message);
