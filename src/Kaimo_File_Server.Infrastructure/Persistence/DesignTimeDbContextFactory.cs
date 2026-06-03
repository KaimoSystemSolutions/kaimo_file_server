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
            // Connection String ist egal — wird nur für die Migration-Generierung gebraucht
            optionsBuilder.UseNpgsql("Host=localhost;Database=dummy");
            return new ApplicationDbContext(optionsBuilder.Options);
        }
    }
}
