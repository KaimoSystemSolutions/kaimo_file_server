using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Core.Domain.Identity
{
    public abstract class Identity
    {
        public Guid Id { get; init; }
        public string Name { get; init; }

        protected Identity() { }

        public Identity(Guid id, string name) 
        { 
            Id = id;
            Name = name;
        }
    }
}
