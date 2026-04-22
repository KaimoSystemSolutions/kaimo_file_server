from impacket.smbconnection import SMBConnection
from io import BytesIO

HOST = "127.0.0.1"
PORT = 4445
SHARE = "test"

def connect():
    conn = SMBConnection(HOST, HOST, sess_port=PORT)
    conn.login("guest", "")
    return conn

def list_shares(conn):
    print("\n[+] Verfügbare Shares:")
    for share in conn.listShares():
        print(f"  - {share['shi1_netname'][:-1]}")

def list_files(conn):
    path = input("Pfad im Share (z.B. \\): ").strip() or "\\"
    print(f"\n[+] Dateien in {path}:")
    for f in conn.listPath(SHARE, path + "*"):
        print(f"  - {f.get_longname()}")

def read_file(conn):
    path = input("Dateipfad (z.B. \\test.txt): ").strip()
    file_obj = BytesIO()
    conn.getFile(SHARE, path, file_obj.write)
    content = file_obj.getvalue()
    
    print("\n[+] Dateiinhalt:")
    try:
        print(content.decode("utf-8"))
    except:
        print(content)

def write_file(conn):
    path = input("Zielpfad (z.B. \\test.txt): ").strip()
    text = input("Inhalt: ")
    
    file_obj = BytesIO(text.encode("utf-8"))
    conn.putFile(SHARE, path, file_obj.read)
    print("[+] Datei geschrieben")

def menu():
    conn = None
    try:
        conn = connect()
        print(f"[+] Verbunden mit {HOST}:{PORT} als guest")

        while True:
            print("\n=== SMB Menü ===")
            print("1) Shares anzeigen")
            print("2) Dateien auflisten")
            print("3) Datei lesen")
            print("4) Datei schreiben")
            print("5) Beenden")

            choice = input("Auswahl: ").strip()

            if choice == "1":
                list_shares(conn)
            elif choice == "2":
                list_files(conn)
            elif choice == "3":
                read_file(conn)
            elif choice == "4":
                write_file(conn)
            elif choice == "5":
                break
            else:
                print("[-] Ungültige Auswahl")

    except Exception as e:
        print(f"[-] Fehler: {e}")
    finally:
        if conn:
            conn.logoff()
            print("[+] Verbindung getrennt")

if __name__ == "__main__":
    menu()