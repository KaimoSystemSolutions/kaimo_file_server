using Microsoft.EntityFrameworkCore;

namespace Kaimo_File_Server.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    /// <summary>Local search index (alternative to Elasticsearch).</summary>
    private void ConfigureSearchIndex(ModelBuilder modelBuilder)
    {
        // Trigram GIN indexes make ILIKE '%term%' on file names and LIKE '%term%' on the word
        // dictionary index-backed, the PostgreSQL counterpart of the Elasticsearch n-gram
        // fields. pg_trgm is a trusted extension (PG 13+), so the database owner can create it.
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.Entity<SearchDocumentRecord>(entity =>
        {
            entity.ToTable("search_documents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(32);
            entity.Property(e => e.FileName).IsRequired();
            entity.Property(e => e.ShareName).IsRequired();
            entity.Property(e => e.AbsolutePath).IsRequired();
            entity.Property(e => e.SharePath).IsRequired();
            entity.Property(e => e.FileType).IsRequired();
            entity.Property(e => e.Content).IsRequired();
            entity.HasIndex(e => e.FileName).HasMethod("gin").HasOperators("gin_trgm_ops");
            // Content matching goes through the word vector; the content itself is only read
            // for the excerpts of shown hits, so it carries no index. tsvector exists on
            // PostgreSQL only; the SQLite test databases never touch the search index.
            if (Database.IsNpgsql())
            {
                entity.Property(e => e.ContentTsv).HasDefaultValueSql("''::tsvector");
                entity.HasIndex(e => e.ContentTsv).HasMethod("gin");
            }
            else
            {
                entity.Ignore(e => e.ContentTsv);
            }
            // Prefix scans (subtree delete/move, folder-scoped search) use LIKE 'x/%'.
            // ponytail: b-tree entries are limited to ~2.7 kB, so a file with a longer absolute
            // path fails to index (logged, skipped); switch to a hashed path column if that matters.
            entity.HasIndex(e => e.AbsolutePath).HasOperators("text_pattern_ops");
            entity.HasIndex(e => new { e.ShareName, e.SharePath }).HasOperators("text_ops", "text_pattern_ops");
        });
        modelBuilder.Entity<SearchTermRecord>(entity =>
        {
            entity.ToTable("search_terms");
            entity.HasKey(e => e.Term);
            entity.HasIndex(e => e.Term).HasMethod("gin").HasOperators("gin_trgm_ops");
        });
    }
}
