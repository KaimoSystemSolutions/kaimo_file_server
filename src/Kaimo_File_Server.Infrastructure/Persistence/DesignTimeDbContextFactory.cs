using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Infrastructure.Persistence
{
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
            // The connection string is irrelevant here — it is only used for generating migrations.
            optionsBuilder.UseNpgsql("Host=localhost;Database=dummy");
            return new ApplicationDbContext(optionsBuilder.Options);
        }
    }
}
