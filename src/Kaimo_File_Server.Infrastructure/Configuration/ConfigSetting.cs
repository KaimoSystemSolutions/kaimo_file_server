using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Infrastructure.Configuration
{
    public class ConfigSetting
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
