using System;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text;

namespace Kaimo_File_Server.Infrastructure.Services
{
    public static class VolumeMountManager
    {
        private static readonly List<string> _volumeMounts = [];
        private static bool IsInitialized = false;

        public static void TrySetVolumeMounts(List<string> newToAdd)
        {
            if (IsInitialized == false)
            {
                _volumeMounts.AddRange(newToAdd);
            }
        }

        public static bool IsPathAccessible(string path)
        {
            return IsPathMounted(path);
        }

        public static bool IsPathMounted(string path)
        {
            if (_volumeMounts.Any(e => path.StartsWith(e)))
                return true;
            else
                return false;
        }
    }
}
