from smb.SMBConnection import SMBConnection
from io import BytesIO

HOST = "127.0.0.1"
PORT = 4445
SERVER_NAME = "MYSERVER"      # NetBIOS-Name des Servers (oder beliebig)
CLIENT_NAME = "MYCLIENT"      # NetBIOS-Name des Clients (oder beliebig)
USERNAME = "marco.hanisch"
PASSWORD = "1234"
DOMAIN = ""                   # Domain, falls nötig

def connect():
    conn = SMBConnection(USERNAME, PASSWORD, CLIENT_NAME, SERVER_NAME,
                         domain=DOMAIN, use_ntlm_v2=True, is_direct_tcp=True)
    connected = conn.connect(HOST, PORT)
    if not connected:
        raise ConnectionError(f"Verbindung zu {HOST}:{PORT} fehlgeschlagen")
    return conn

def list_shares(conn):
    print("\n[+] Verfügbare Shares:")
    shares = conn.listShares()
    for i, share in enumerate(shares):
        print(f"  {i+1}) {share.name}")
    return shares

def choose_share(conn):
    shares = list_shares(conn)
    choice = input("Share-Nummer wählen: ").strip()
    try:
        idx = int(choice) - 1
        if 0 <= idx < len(shares):
            return shares[idx].name
    except ValueError:
        pass
    print("[-] Ungültige Auswahl")
    return None

def list_files(conn):
    share = choose_share(conn)
    if not share:
        return
    path = input("Pfad im Share (z.B. /): ").strip() or "/"
    print(f"\n[+] Dateien in '{share}{path}':")
    for f in conn.listPath(share, path):
        print(f"  - {f.filename}")

def read_file(conn):
    share = choose_share(conn)
    if not share:
        return
    path = input("Dateipfad (z.B. /test.txt): ").strip()

    file_obj = BytesIO()
    conn.retrieveFile(share, path, file_obj)
    content = file_obj.getvalue()

    print("\n[+] Dateiinhalt:")
    try:
        print(content.decode("utf-8"))
    except UnicodeDecodeError:
        print(content)

def write_file(conn):
    share = choose_share(conn)
    if not share:
        return
    path = input("Zielpfad (z.B. /test.txt): ").strip()
    text = input("Inhalt: ")

    file_obj = BytesIO(text.encode("utf-8"))
    conn.storeFile(share, path, file_obj)
    print("[+] Datei geschrieben")

def menu():
    conn = None
    try:
        conn = connect()
        print(f"[+] Verbunden mit {HOST}:{PORT} als {USERNAME}")

        while True:
            print("\n=== SMB Menü (pysmb) ===")
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
            conn.close()
            print("[+] Verbindung getrennt")

if __name__ == "__main__":
    menu()