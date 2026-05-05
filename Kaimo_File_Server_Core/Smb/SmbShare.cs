using Kaimo_File_Server_Core.Core.Repositories;
using SMBLibrary;
using SMBLibrary.Server;

namespace Kaimo_File_Server_Core.Smb
{
    public class SmbShare : ISMBShare
    {
        public string Name { get; }
        public INTFileStore FileStore { get; }

        private readonly IServiceProvider _serviceProvider;

        public SmbShare(string name, INTFileStore fileStore, IServiceProvider serviceProvider)
        {
            Name = name;
            FileStore = fileStore;
            _serviceProvider = serviceProvider;
        }

        public bool HasAccess(SecurityContext context, AccessMask desiredAccess)
        {
            var userName = context.UserName;
            Console.WriteLine($"[ShareAccess] User '{userName}' will auf Share '{Name}' zugreifen");

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var shareRepo = scope.ServiceProvider.GetRequiredService<IShareAccessRepository>();

                var user = userRepo.GetByUsernameAsync(userName).GetAwaiter().GetResult();
                if (user == null)
                {
                    Console.WriteLine($"[-] User '{userName}' nicht gefunden");
                    return false;
                }

                if (shareRepo.HasAccessAsync(Name, user.Id).GetAwaiter().GetResult())
                {
                    Console.WriteLine($"[+] User '{userName}' hat direkten Zugriff auf '{Name}'");
                    return true;
                }

                // TODO: Gruppen-Berechtigung pruefen

                Console.WriteLine($"[-] User '{userName}' hat keinen Zugriff auf '{Name}'");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ShareAccess ERROR] {userName} -> {Name}: {ex.Message}");
                return false;
            }
        }
    }
}